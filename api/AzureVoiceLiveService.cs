using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.Logging;

namespace CallAutomation.AzureAI.VoiceLive
{
    /// <summary>
    /// Azure Voice Live Service — bridges ACS media stream to Azure Voice Live API
    /// via raw WebSocket. Mirrors the production ContactCenterAgent implementation
    /// 1:1 (azure_semantic_vad, deep noise suppression, server echo cancellation,
    /// HD voice with style/temperature, greeting protection, hang_up tool with
    /// farewell sequencing).
    ///
    /// Authentication: api-key header (single shared key from appsettings).
    /// Voice: en-US-Andrew:DragonHDOmniLatestNeural with style="shouting" — Omni
    /// multilingual auto-detect, AI replies in whatever language the caller speaks.
    /// </summary>
    public class AzureVoiceLiveService
    {
        // ACS media format (24 kHz pcm16 mono — matches MediaStreamingOptions in Program.cs)
        private const int VoiceLiveBytesPerSecond = 24000 * 2;

        private readonly CancellationTokenSource m_cts = new();
        private readonly AcsMediaStreamingHandler m_mediaStreaming;
        private readonly IConfiguration m_configuration;
        private readonly ILogger<AzureVoiceLiveService> m_logger;

        private ClientWebSocket m_ws = null!;
        private Func<string, Task>? m_onHangUp;

        // ── Hang-up sequencing state ─────────────────────────────────────────────
        // The model's tool-call response carries no audio. After receiving it we send
        // a fresh response.create asking for a spoken farewell, then disconnect after
        // the farewell audio actually finishes playing on the PSTN side.
        private bool m_pendingHangUp;
        private string? m_pendingHangUpReason;
        private string? m_hangUpResponseId;
        private long m_farewellAudioBytes;
        private bool m_farewellDisconnectScheduled;

        // ── Greeting protection state ────────────────────────────────────────────
        // First AI turn is the greeting. Mute barge-in until the greeting actually
        // finishes playing on the phone, then re-enable normal turn detection.
        private bool m_greetingInFlight = true;
        private bool m_greetingDelayScheduled;
        private long m_greetingAudioBytesSent;

        private static readonly JsonSerializerOptions s_compactJson = new() { WriteIndented = false };

        // System prompt — generic shouting multilingual Andrew (no Norlys branding).
        // First utterance is just "HEJ!" then waits for the caller to respond.
        private const string SystemPrompt =
            @"You are Andrew, a wildly enthusiastic phone agent who SHOUTS EVERYTHING with extreme energy and excitement.

OPENING (MANDATORY FIRST UTTERANCE):
- Say ONLY the word 'HEJ!' as your very first utterance — short, loud, friendly. Then STOP and wait for the caller to respond.
- Do NOT introduce yourself. Do NOT explain anything. Just 'HEJ!' — one word — then wait.

LANGUAGE RULES:
- Detect the language of the caller's first response.
- Reply in that SAME language for the rest of the call. Switch languages instantly if the caller switches language.
- Even if your accent is comically bad in that language — embrace it, lean into it.

PERSONALITY:
- You are LOUD, EXCITED, and AMAZED by everything the caller says.
- Keep every reply to ONE or TWO short sentences max.
- Be helpful and friendly, but always with maximum enthusiasm.

ENDING THE CALL:
- If the caller says goodbye, 'farvel', 'hej hej', 'tak for nu', 'bye', 'ciao', or otherwise signals they want to end the call, you MUST first SHOUT a short farewell in their language (e.g. 'TAK FOR OPKALDET, HAV EN FANTASTISK DAG, FARVEL!'), then call the hang_up tool.
- Trust the caller. Do not push topics, do not ask 'are you sure?', do not try one more time.";

        public void OnHangUp(Func<string, Task> callback) => m_onHangUp = callback;

        public AzureVoiceLiveService(
            AcsMediaStreamingHandler mediaStreaming,
            IConfiguration configuration,
            ILogger<AzureVoiceLiveService> logger)
        {
            m_mediaStreaming = mediaStreaming;
            m_configuration = configuration;
            m_logger = logger;
            m_logger.LogInformation("AzureVoiceLiveService initialized");
        }

        /// <summary>
        /// Connect, configure session, kick off first response. Must be awaited
        /// before any audio flows. Separated from constructor to avoid sync-over-async.
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                var endpoint = m_configuration.GetValue<string>("AzureVoiceLiveEndpoint");
                ArgumentException.ThrowIfNullOrEmpty(endpoint);

                var apiKey = m_configuration.GetValue<string>("AzureVoiceLiveApiKey");
                ArgumentException.ThrowIfNullOrEmpty(apiKey);

                var model = m_configuration.GetValue<string>("VoiceLiveModel") ?? "gpt-realtime";

                m_logger.LogInformation("Connecting to Azure Voice Live: {Endpoint}, model: {Model}", endpoint, model);

                var url = new Uri(
                    $"{endpoint.TrimEnd('/').Replace("https", "wss")}/voice-live/realtime" +
                    $"?api-version=2025-10-01&x-ms-client-request-id={Guid.NewGuid()}&model={model}");

                m_ws = new ClientWebSocket();
                m_ws.Options.SetRequestHeader("api-key", apiKey);
                await m_ws.ConnectAsync(url, CancellationToken.None);
                m_logger.LogInformation("Voice Live WebSocket connected");

                // Greeting protection: default ON. Set VoiceLive:ProtectFirstResponse=false to disable.
                m_greetingInFlight = m_configuration.GetValue("VoiceLive:ProtectFirstResponse", true);
                m_logger.LogInformation("Greeting barge-in protection: {Enabled}", m_greetingInFlight);

                StartConversation();
                await UpdateSessionAsync();
                await SendMessageAsync(JsonSerializer.Serialize(new { type = "response.create" }, s_compactJson));

                m_logger.LogInformation("Voice Live session fully initialized and ready");
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "Error during Voice Live initialization");
                throw;
            }
        }

        private async Task UpdateSessionAsync()
        {
            // VAD knobs — mirrors ContactCenterAgent / danish-voice-lab defaults exactly.
            var vadType = m_configuration.GetValue<string>("VoiceLive:Vad:Type") ?? "azure_semantic_vad";
            var vadThreshold = m_configuration.GetValue("VoiceLive:Vad:Threshold", 0.3);
            var vadPrefixPaddingMs = m_configuration.GetValue("VoiceLive:Vad:PrefixPaddingMs", 300);
            var vadSilenceMs = m_configuration.GetValue("VoiceLive:Vad:SilenceDurationMs", 500);

            m_logger.LogInformation(
                "VAD config: type={Type}, threshold={Threshold}, prefix={Prefix}ms, silence={Silence}ms",
                vadType, vadThreshold, vadPrefixPaddingMs, vadSilenceMs);

            var session = new
            {
                type = "session.update",
                session = new
                {
                    modalities = new[] { "text", "audio" },
                    input_audio_format = "pcm16",
                    output_audio_format = "pcm16",
                    instructions = SystemPrompt,
                    // During greeting: server won't cancel TTS on user speech, won't auto-fire
                    // a new response on user speech_stopped. After greeting playback we resend
                    // session.update with both back to defaults (true).
                    turn_detection = m_greetingInFlight
                        ? (object)new
                        {
                            type = vadType,
                            threshold = vadThreshold,
                            prefix_padding_ms = vadPrefixPaddingMs,
                            silence_duration_ms = vadSilenceMs,
                            interrupt_response = false,
                            create_response = false
                        }
                        : new
                        {
                            type = vadType,
                            threshold = vadThreshold,
                            prefix_padding_ms = vadPrefixPaddingMs,
                            silence_duration_ms = vadSilenceMs
                        },
                    input_audio_noise_reduction = new { type = "azure_deep_noise_suppression" },
                    input_audio_echo_cancellation = new { type = "server_echo_cancellation" },
                    input_audio_transcription = BuildTranscriptionConfig(),
                    voice = BuildVoiceConfig(),
                    tools = new[]
                    {
                        new
                        {
                            type = "function",
                            name = "hang_up",
                            description =
                                "End the phone call. Call this tool when the caller signals they want to end the conversation in ANY language and ANY phrasing " +
                                "(e.g. 'bye', 'goodbye', 'farvel', 'hej hej', 'tak for nu', 'ciao', 'auf wiedersehen'). " +
                                "MANDATORY: BEFORE calling this tool, you MUST first SHOUT a brief farewell in the caller's language. " +
                                "Only avoid calling it when the caller is clearly still engaged (asking questions, sharing information, mid-sentence).",
                            parameters = new
                            {
                                type = "object",
                                properties = new
                                {
                                    reason = new
                                    {
                                        type = "string",
                                        description = "Brief reason for ending the call."
                                    }
                                },
                                required = new[] { "reason" }
                            }
                        }
                    }
                }
            };

            await SendMessageAsync(JsonSerializer.Serialize(session, s_compactJson));
            m_logger.LogInformation("session.update sent");
        }

        /// <summary>
        /// Voice = en-US-Andrew:DragonHDOmniLatestNeural (HD Omni — multilingual auto-detect),
        /// style = shouting, temperature = 0.9 (max comedic variety). All knobs overridable
        /// via Voice:* in appsettings.json.
        /// </summary>
        private Dictionary<string, object> BuildVoiceConfig()
        {
            var voiceType = m_configuration.GetValue<string>("Voice:Type") ?? "azure-standard";
            var voiceName = m_configuration.GetValue<string>("Voice:Name") ?? "en-US-Andrew:DragonHDOmniLatestNeural";
            var voiceTemp = m_configuration.GetValue("Voice:Temperature", 0.9);
            var voiceStyle = m_configuration.GetValue<string>("Voice:Style") ?? "shouting";

            var voice = new Dictionary<string, object>
            {
                ["type"] = voiceType,
                ["name"] = voiceName
            };

            // Send temperature + style ONLY for HD / HD Omni voices (name contains ":DragonHD").
            // Standard neural voices reject/ignore them.
            var isHd = voiceName.Contains(":DragonHD", StringComparison.OrdinalIgnoreCase);
            if (voiceType == "azure-standard" && isHd)
            {
                voice["temperature"] = voiceTemp;
                if (!string.IsNullOrWhiteSpace(voiceStyle))
                    voice["style"] = voiceStyle;

                // No locale pinned — Andrew is en-US but Omni multilingual auto-detect
                // handles whatever language the LLM emits. Pinning a locale would force
                // a specific accent on every language.
            }

            m_logger.LogInformation("Voice config: type={Type}, name={Name}, temperature={Temp}, style={Style}",
                voiceType, voiceName,
                voice.ContainsKey("temperature") ? voice["temperature"] : "(omitted)",
                voice.ContainsKey("style") ? voice["style"] : "(omitted)");

            return voice;
        }

        /// <summary>
        /// Whisper-1 for STT — auto-detects language across ~50 locales, perfect for the
        /// "AI replies in whatever language you speak" demo. Override via Transcription:Model.
        /// </summary>
        private Dictionary<string, object> BuildTranscriptionConfig()
        {
            var model = m_configuration.GetValue<string>("Transcription:Model") ?? "whisper-1";
            var lang = m_configuration.GetValue<string>("Transcription:Language");

            var config = new Dictionary<string, object> { ["model"] = model };
            if (!string.IsNullOrEmpty(lang))
                config["language"] = lang;

            m_logger.LogInformation("Transcription config: model={Model}, language={Language}",
                model, lang ?? "(auto)");
            return config;
        }

        private async Task SendMessageAsync(string message, CancellationToken ct = default)
        {
            if (m_ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(message);
            await m_ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        private async Task ReceiveMessagesAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            try
            {
                while (m_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await m_ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            m_logger.LogWarning("Voice Live WebSocket received Close frame");
                            await m_ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            return;
                        }
                    } while (!result.EndOfMessage);

                    var raw = sb.ToString();
                    var data = JsonSerializer.Deserialize<Dictionary<string, object>>(raw);
                    if (data == null) continue;

                    var msgType = data["type"].ToString();

                    if (msgType == "session.updated")
                    {
                        m_logger.LogInformation("Session accepted by server");
                    }
                    else if (msgType == "response.audio.delta")
                    {
                        var audioBytes = Convert.FromBase64String(data["delta"].ToString()!);
                        if (m_greetingInFlight) m_greetingAudioBytesSent += audioBytes.Length;
                        if (m_pendingHangUp) m_farewellAudioBytes += audioBytes.Length;
                        await m_mediaStreaming.SendMessageAsync(OutStreamingData.GetAudioDataForOutbound(audioBytes));
                    }
                    else if (msgType == "input_audio_buffer.speech_started")
                    {
                        if (m_greetingInFlight)
                        {
                            // Server has interrupt_response=false during greeting, so it won't
                            // auto-cancel TTS. We must mirror that on the client side — DO NOT
                            // send StopAudio or response.cancel, or the greeting dies anyway.
                            m_logger.LogInformation("VAD started during greeting — IGNORING (greeting protection)");
                        }
                        else
                        {
                            m_logger.LogInformation("VAD started — barge-in, cancelling AI response");
                            await m_mediaStreaming.SendMessageAsync(OutStreamingData.GetStopAudioForOutbound());
                            await SendMessageAsync(JsonSerializer.Serialize(new { type = "response.cancel" }, s_compactJson));
                        }
                    }
                    else if (msgType == "input_audio_buffer.speech_stopped")
                    {
                        m_logger.LogInformation("VAD ended");
                    }
                    else if (msgType == "conversation.item.input_audio_transcription.completed")
                    {
                        var transcript = data.ContainsKey("transcript") ? data["transcript"]?.ToString() : "";
                        m_logger.LogInformation("User transcript: {Transcript}", transcript);
                    }
                    else if (msgType == "response.audio_transcript.done")
                    {
                        var transcript = data.ContainsKey("transcript") ? data["transcript"]?.ToString() : "";
                        m_logger.LogInformation("AI transcript: {Transcript}", transcript);
                    }
                    else if (msgType == "response.function_call_arguments.done")
                    {
                        var funcName = data.ContainsKey("name") ? data["name"]?.ToString() : "";
                        if (funcName == "hang_up")
                        {
                            await HandleHangUpAsync(data);
                        }
                    }
                    else if (msgType == "response.done")
                    {
                        await HandleResponseDoneAsync(data);
                    }
                    else if (msgType == "error")
                    {
                        m_logger.LogError("Voice Live error: {Message}", raw);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                m_logger.LogInformation("Response streaming cancelled");
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "Error while receiving from Voice Live");
            }
        }

        private async Task HandleHangUpAsync(Dictionary<string, object> data)
        {
            var callId = data.ContainsKey("call_id") ? data["call_id"]?.ToString() : "";
            var args = data.ContainsKey("arguments") ? data["arguments"]?.ToString() : "";
            m_logger.LogInformation("AI invoked hang_up tool. Args: {Args}", args);

            m_pendingHangUp = true;
            m_pendingHangUpReason = args ?? "AI initiated hang up";
            m_hangUpResponseId = data.ContainsKey("response_id") ? data["response_id"]?.ToString() : null;
            m_farewellAudioBytes = 0;
            m_farewellDisconnectScheduled = false;

            // Acknowledge the tool call.
            var toolOutput = new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "function_call_output",
                    call_id = callId,
                    output = "{\"success\":true}"
                }
            };
            await SendMessageAsync(JsonSerializer.Serialize(toolOutput));

            // Force a fresh response with explicit per-response instructions for the farewell.
            // Per-response instructions OVERRIDE the session prompt for this turn — most reliable
            // way to make the model say a specific line under barge-in pressure.
            var farewellResponse = new
            {
                type = "response.create",
                response = new
                {
                    modalities = new[] { "audio", "text" },
                    instructions =
                        "SHOUT ONE short, enthusiastic farewell in the SAME LANGUAGE the caller has been speaking. " +
                        "Examples: Danish 'TAK FOR OPKALDET, HAV EN FANTASTISK DAG, FARVEL!' — English 'THANKS FOR THE CALL, HAVE A GREAT DAY, GOODBYE!' " +
                        "Do NOT say anything else. Do NOT ask any questions. Do NOT call any tools."
                }
            };
            await SendMessageAsync(JsonSerializer.Serialize(farewellResponse));
            m_logger.LogInformation("Farewell response triggered (hang_up response_id: {Id})", m_hangUpResponseId ?? "<unknown>");

            // Safety net: force-disconnect after 12s if farewell never completes.
            _ = Task.Run(async () =>
            {
                await Task.Delay(12000);
                if (m_pendingHangUp && !m_farewellDisconnectScheduled)
                {
                    m_logger.LogWarning("Farewell did not complete within 12s — forcing disconnect");
                    m_farewellDisconnectScheduled = true;
                    if (m_onHangUp != null)
                        await m_onHangUp(m_pendingHangUpReason ?? "AI initiated hang up — farewell timeout");
                }
            });
        }

        private async Task HandleResponseDoneAsync(Dictionary<string, object> data)
        {
            if (m_pendingHangUp)
            {
                // The hang_up tool-call response carries no audio; its response.done arrives ~1ms
                // after we send response.create for the farewell. Ignore it; act on the NEXT one.
                string? thisResponseId = null;
                try
                {
                    if (data.ContainsKey("response") && data["response"] is JsonElement respElem &&
                        respElem.ValueKind == JsonValueKind.Object &&
                        respElem.TryGetProperty("id", out var idElem))
                    {
                        thisResponseId = idElem.GetString();
                    }
                }
                catch { /* best-effort */ }

                if (!string.IsNullOrEmpty(m_hangUpResponseId) && thisResponseId == m_hangUpResponseId)
                {
                    m_logger.LogInformation("Ignoring response.done for hang_up tool-call response {Id} — waiting for farewell", thisResponseId);
                    return;
                }

                // Farewell response — wait for actual PSTN playback to finish, then disconnect.
                m_farewellDisconnectScheduled = true;
                const int SafetyTailMs = 500;
                var bytes = m_farewellAudioBytes;
                var playbackMs = (int)(bytes * 1000L / VoiceLiveBytesPerSecond) + SafetyTailMs;
                playbackMs = Math.Clamp(playbackMs, 1500, 8000);
                m_logger.LogInformation("Farewell response.done — disconnecting in {Delay}ms ({Bytes} bytes)", playbackMs, bytes);
                await Task.Delay(playbackMs);

                if (m_onHangUp != null)
                    await m_onHangUp(m_pendingHangUpReason ?? "AI initiated hang up");
                else
                    m_logger.LogWarning("hang_up requested but no OnHangUp callback registered");
                return;
            }

            if (m_greetingInFlight && !m_greetingDelayScheduled)
            {
                // Schedule the protection-off session.update for AFTER the greeting actually
                // finishes playing on the phone. response.done fires when the SERVER finishes
                // generating audio (sub-second), not when PSTN has played it.
                m_greetingDelayScheduled = true;
                const int SafetyTailMs = 300;
                var bytes = m_greetingAudioBytesSent;
                var playbackMs = (int)(bytes * 1000L / VoiceLiveBytesPerSecond) + SafetyTailMs;
                m_logger.LogInformation(
                    "Greeting response.done; holding protection {DelayMs}ms while {Bytes} bytes finish playing",
                    playbackMs, bytes);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(playbackMs);
                        m_greetingInFlight = false;

                        // Discard ANY audio captured during the greeting — without this, what
                        // the customer said while the greeting was playing is committed as a
                        // user turn, and the AI replies to it the moment we re-enable normal VAD.
                        await SendMessageAsync(JsonSerializer.Serialize(
                            new { type = "input_audio_buffer.clear" }, s_compactJson));
                        m_logger.LogInformation("Cleared input audio buffer (discarding mid-greeting speech)");

                        m_logger.LogInformation("Greeting playback estimated complete — re-enabling normal turn detection");
                        await UpdateSessionAsync();
                    }
                    catch (Exception ex)
                    {
                        m_logger.LogWarning(ex, "Failed to re-enable barge-in after greeting");
                        m_greetingInFlight = false;
                    }
                });
            }

            m_logger.LogInformation("Model turn finished");
        }

        public void StartConversation()
        {
            _ = Task.Run(() => ReceiveMessagesAsync(m_cts.Token));
        }

        public async Task SendAudioToExternalAI(byte[] data)
        {
            var msg = JsonSerializer.Serialize(new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(data)
            }, s_compactJson);
            await SendMessageAsync(msg);
        }

        public async Task Close()
        {
            try
            {
                m_logger.LogInformation("Closing Voice Live service");
                m_cts.Cancel();
                if (m_ws is { State: WebSocketState.Open })
                {
                    await m_ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Normal", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "Error closing service");
            }
            finally
            {
                m_cts.Dispose();
            }
        }
    }
}
