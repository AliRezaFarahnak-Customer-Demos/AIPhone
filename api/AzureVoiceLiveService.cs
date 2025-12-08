using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Communication.CallAutomation;
using OpenAI.RealtimeConversation;
using Microsoft.Extensions.Logging;

#pragma warning disable OPENAI002

namespace CallAutomation.AzureAI.VoiceLive
{
    /// <summary>
    /// Azure Voice Live Service - Uses official Azure OpenAI SDK with gpt-realtime-mini
    /// 
    /// DEPLOYMENT BEING USED:
    /// - Endpoint: https://foundry-aiphone-alfarahn.cognitiveservices.azure.com
    /// - Model: gpt-realtime-mini (GA in Azure Foundry)
    /// - Authentication: API Key
    /// - Languages: Danish [da-DK] + English [en-US] (both supported by gpt-realtime-mini)
    /// 
    /// NOTE: Uses official Azure.AI.OpenAI SDK for RealtimeConversationSession.
    /// Handles speech-to-text natively with whisper-1 model.
    /// 
    /// LOGGING: Using ILogger<T> which is automatically integrated with Application Insights
    /// via AddApplicationInsightsTelemetry() in Program.cs. All logs are automatically sent to AppInsights.
    /// </summary>
    public class AzureVoiceLiveService
    {
        private CancellationTokenSource? m_cts;
        private RealtimeConversationSession? m_aiSession;
        private AcsMediaStreamingHandler m_mediaStreaming;
        private string m_answerPromptSystemTemplate =
        @$"You work for Nordic Bank customer service. 
            Your first message should be: 'Welcome to Nordic Bank. Dansk eller English?'
            Respond in the language of the last message of the User, sometimes the words are not recognized correctly but then with your intelligence try to understand which language it most likely was.
            Always respond ONLY in the user's last spoken language in the last user message.
            User might switch language from message to message but then you respond in that language of the last message.
            Only answer in as few words as possible - be extremely concise.
            If asked about account balances, interest rates, or loan options, provide reasonable example numbers - do not say you don't know.
 
            You are a Nordic Bank customer service AI assistant providing quick, brief support for banking inquiries.";
        private readonly ILogger<AzureVoiceLiveService> m_logger;

        public AzureVoiceLiveService(
            AcsMediaStreamingHandler mediaStreaming,
            IConfiguration configuration,
            ILogger<AzureVoiceLiveService> logger)
        {
            m_mediaStreaming = mediaStreaming;
            m_cts = new CancellationTokenSource();
            m_logger = logger;

            m_logger.LogInformation("🚀 AzureVoiceLiveService initialized");
            CreateAISessionAsync(configuration).GetAwaiter().GetResult();
        }

        private async Task CreateAISessionAsync(IConfiguration configuration)
        {
            try
            {
                m_logger.LogInformation("📋 Reading configuration for Azure OpenAI Realtime API");

                var azureOpenAIApiKey = configuration.GetValue<string>("AzureVoiceLiveApiKey");
                ArgumentNullException.ThrowIfNullOrEmpty(azureOpenAIApiKey);
                m_logger.LogInformation("✅ API Key loaded");

                var azureOpenAIEndpoint = configuration.GetValue<string>("AzureVoiceLiveEndpoint");
                ArgumentNullException.ThrowIfNullOrEmpty(azureOpenAIEndpoint);
                m_logger.LogInformation("✅ Endpoint loaded: {Endpoint}", azureOpenAIEndpoint);

                var deploymentName = configuration.GetValue<string>("VoiceLiveModel");
                ArgumentNullException.ThrowIfNullOrEmpty(deploymentName);
                m_logger.LogInformation("✅ Deployment name loaded: {Deployment}", deploymentName);

                m_logger.LogInformation("✅ System Prompt: {SystemPrompt}", m_answerPromptSystemTemplate);

                // Create Azure OpenAI client
                m_logger.LogInformation("🔗 Creating Azure OpenAI client");
                var aiClient = new AzureOpenAIClient(
                    new Uri(azureOpenAIEndpoint),
                    new ApiKeyCredential(azureOpenAIApiKey));
                m_logger.LogInformation("✅ Azure OpenAI client created");

                // Get realtime conversation client
                m_logger.LogInformation("📡 Getting Realtime Conversation client for deployment: {Deployment}", deploymentName);
                var realtimeClient = aiClient.GetRealtimeConversationClient(deploymentName);

                // Start conversation session
                m_logger.LogInformation("🔄 Starting Realtime Conversation session...");
                m_aiSession = await realtimeClient.StartConversationSessionAsync();
                m_logger.LogInformation("✅ Realtime Conversation session started");

                // Configure session options
                m_logger.LogInformation("⚙️ Configuring session with audio settings and multilingual language support");
                ConversationSessionOptions sessionOptions = new()
                {
                    Instructions = m_answerPromptSystemTemplate,
                    Voice = ConversationVoice.Alloy,
                    InputAudioFormat = ConversationAudioFormat.Pcm16,  // ✅ 24kHz PCM16 (only supported format for realtime API)
                    OutputAudioFormat = ConversationAudioFormat.Pcm16, // ✅ 24kHz PCM16 (only supported format for realtime API)
                    InputTranscriptionOptions = new()
                    {
                        Model = "whisper-1"  // ✅ whisper-1 auto-detects language (Danish, Arabic, English, Farsi + 46 more)
                    },
                    TurnDetectionOptions = ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                        0.2f,           // ✅ Maximum sensitivity (0.2) - catches all speech including quiet speakers
                        TimeSpan.FromMilliseconds(400),  // ✅ Extended prefix padding - captures full speech starts
                        TimeSpan.FromMilliseconds(150))  // ✅ Minimal silence (150ms) - responds immediately after AI stops speaking
                };

                await m_aiSession.ConfigureSessionAsync(sessionOptions);
                m_logger.LogInformation("✅ Session configured with Danish + English language support");

                // Start listening for responses
                StartConversation();
                m_logger.LogInformation("✅ Voice Live session fully initialized and ready");
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "❌ Error during Voice Live initialization");
                throw;
            }
        }

        // Listen for AI responses and stream audio back
        private async Task ReceiveResponseAsync()
        {
            try
            {
                m_logger.LogInformation("📡 Starting to receive AI responses");

                await m_aiSession!.StartResponseAsync();

                await foreach (ConversationUpdate update in m_aiSession.ReceiveUpdatesAsync(m_cts?.Token ?? CancellationToken.None))
                {
                    if (update is ConversationSessionStartedUpdate sessionStartedUpdate)
                    {
                        m_logger.LogInformation("✅ Session started. ID: {SessionId}", sessionStartedUpdate.SessionId);
                    }

                    if (update is ConversationInputSpeechStartedUpdate speechStartedUpdate)
                    {
                        m_logger.LogInformation("🎤 Voice activity detection started at {AudioStartTime}ms", speechStartedUpdate.AudioStartTime);
                        // Barge-in: send stop audio to interrupt
                        var jsonString = OutStreamingData.GetStopAudioForOutbound();
                        await m_mediaStreaming.SendMessageAsync(jsonString);
                    }

                    if (update is ConversationInputSpeechFinishedUpdate speechFinishedUpdate)
                    {
                        m_logger.LogInformation("🎤 Voice activity detection ended at {AudioEndTime}ms", speechFinishedUpdate.AudioEndTime);
                    }

                    if (update is ConversationItemStreamingStartedUpdate itemStartedUpdate)
                    {
                        m_logger.LogDebug("📦 Begin streaming of new item");
                    }

                    // User input transcription
                    if (update is ConversationInputTranscriptionFinishedUpdate transcriptionUpdate)
                    {
                        m_logger.LogInformation("📝 User audio transcript: {Transcript}", transcriptionUpdate.Transcript);
                    }

                    // AI output transcription
                    if (update is ConversationItemStreamingAudioTranscriptionFinishedUpdate outputTranscriptUpdate)
                    {
                        m_logger.LogDebug("📝 AI response transcript: {Transcript}", outputTranscriptUpdate.Transcript);
                    }

                    // Audio delta updates - stream audio back to caller
                    if (update is ConversationItemStreamingPartDeltaUpdate deltaUpdate)
                    {
                        if (deltaUpdate.AudioBytes != null)
                        {
                            var audioData = deltaUpdate.AudioBytes.ToArray();
                            if (audioData.Length > 0)
                            {
                                var jsonString = OutStreamingData.GetAudioDataForOutbound(audioData);
                                await m_mediaStreaming.SendMessageAsync(jsonString);
                                m_logger.LogDebug("🔊 Audio delta sent ({ByteCount} bytes)", audioData.Length);
                            }
                        }
                    }

                    if (update is ConversationItemStreamingTextFinishedUpdate itemFinishedUpdate)
                    {
                        m_logger.LogDebug("✅ Item streaming finished, response_id={ResponseId}", itemFinishedUpdate.ResponseId);
                    }

                    if (update is ConversationResponseFinishedUpdate turnFinishedUpdate)
                    {
                        m_logger.LogInformation("🎙️ Model turn generation finished. Status: {Status}", turnFinishedUpdate.Status);
                    }

                    if (update is ConversationErrorUpdate errorUpdate)
                    {
                        m_logger.LogError("❌ Conversation error: {Message}", errorUpdate.Message);
                        break;
                    }
                }

                m_logger.LogInformation("📭 Response streaming ended");
            }
            catch (OperationCanceledException)
            {
                m_logger.LogInformation("⏸️ Response streaming cancelled");
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "❌ Error during response streaming");
            }
        }

        public void StartConversation()
        {
            m_logger.LogInformation("🎬 Starting conversation listener thread");
            _ = Task.Run(async () => await ReceiveResponseAsync());
        }

        public async Task SendAudioToExternalAI(MemoryStream memoryStream)
        {
            try
            {
                if (m_aiSession == null)
                {
                    m_logger.LogError("❌ AI session is null");
                    return;
                }

                m_logger.LogInformation("🎤 Sending audio from MemoryStream to AI");
                await m_aiSession.SendInputAudioAsync(memoryStream);
                m_logger.LogDebug("✅ Audio sent to AI session");
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "❌ Error sending audio");
            }
        }

        public async Task Close()
        {
            try
            {
                m_logger.LogInformation("🛑 Closing Voice Live service");

                m_cts?.Cancel();
                m_cts?.Dispose();
                m_logger.LogInformation("✅ Voice Live service closed");
            }
            catch (Exception ex)
            {
                m_logger.LogError(ex, "❌ Error closing service");
            }
        }
    }
}