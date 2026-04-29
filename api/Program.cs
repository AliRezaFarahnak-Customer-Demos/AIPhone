using Azure.Communication.CallAutomation;
using Azure.Identity;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using CallAutomation.AzureAI.VoiceLive;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;

var builder = WebApplication.CreateBuilder(args);

// Add local settings for development (file is in .gitignore)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// App Insights — SDK auto-detects APPLICATIONINSIGHTS_CONNECTION_STRING env var (set by Bicep/Container App)
builder.Services.AddApplicationInsightsTelemetry();

// Get ACS Connection String from config
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

// Call Automation Client
var client = new CallAutomationClient(acsConnectionString);

// Credential singleton for Azure Voice Live (managed identity in Azure, az-cli locally)
var aiCredential = new DefaultAzureCredential();

// Pre-warm the AI token AND validate Voice Live WebSocket connectivity at startup.
// Catches RBAC/auth issues immediately instead of silently failing on the first call.
try
{
    var warmupStart = DateTime.UtcNow;
    var tokenResult = await aiCredential.GetTokenAsync(
        new Azure.Core.TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }),
        CancellationToken.None);
    var warmupMs = (DateTime.UtcNow - warmupStart).TotalMilliseconds;
    Console.WriteLine($"✓ AI credential pre-warmed in {warmupMs:F0}ms (expires {tokenResult.ExpiresOn:HH:mm:ss})");

    var voiceLiveEndpoint = builder.Configuration.GetValue<string>("AzureOpenAI:Endpoint");
    var voiceLiveModel = builder.Configuration.GetValue<string>("AzureOpenAI:DeploymentName") ?? "gpt-realtime";
    if (!string.IsNullOrEmpty(voiceLiveEndpoint))
    {
        var wsUrl = new Uri($"{voiceLiveEndpoint.TrimEnd('/').Replace("https", "wss")}/voice-live/realtime?api-version=2025-10-01&x-ms-client-request-id={Guid.NewGuid()}&model={voiceLiveModel}");
        using var probeWs = new System.Net.WebSockets.ClientWebSocket();
        probeWs.Options.SetRequestHeader("Authorization", $"Bearer {tokenResult.Token}");
        using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await probeWs.ConnectAsync(wsUrl, probeCts.Token);
        await probeWs.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "startup probe", CancellationToken.None);
        Console.WriteLine($"✓ Voice Live WebSocket probe OK (model: {voiceLiveModel})");
    }
}
catch (System.Net.WebSockets.WebSocketException ex) when (ex.Message.Contains("401"))
{
    Console.WriteLine($"✗ FATAL: Voice Live WebSocket returned 401 — RBAC roles missing on managed identity!");
    Console.WriteLine($"  Need 'Cognitive Services OpenAI User' AND 'Cognitive Services User' roles.");
}
catch (Exception ex)
{
    Console.WriteLine($"⚠ AI credential/Voice Live pre-warm failed (will retry on first call): {ex.Message}");
}

// Track active call connection IDs (keyed by WebSocket contextId) so the hang-up callback can disconnect the call
var callConnections = new ConcurrentDictionary<string, string>();

var app = builder.Build();

// Get app base URL from multiple sources (priority order)
var appBaseUrl = Environment.GetEnvironmentVariable("VS_TUNNEL_URL")?.TrimEnd('/');

if (string.IsNullOrEmpty(appBaseUrl))
{
    appBaseUrl = builder.Configuration.GetValue<string>("AppBaseUrl");
}

if (string.IsNullOrEmpty(appBaseUrl))
{
    // Container Apps sets CONTAINER_APP_HOSTNAME (e.g. ca-caller-agent.<env>.azurecontainerapps.io)
    var containerAppHostname = Environment.GetEnvironmentVariable("CONTAINER_APP_HOSTNAME");
    if (!string.IsNullOrEmpty(containerAppHostname))
    {
        appBaseUrl = $"https://{containerAppHostname}";
    }
}

if (string.IsNullOrEmpty(appBaseUrl))
{
    var websiteHostname = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
    if (!string.IsNullOrEmpty(websiteHostname))
    {
        appBaseUrl = $"https://{websiteHostname}";
    }
}

if (string.IsNullOrEmpty(appBaseUrl))
{
    throw new InvalidOperationException("AppBaseUrl must be configured");
}

Console.WriteLine($"✓ App Base URL: {appBaseUrl}");

// Build number — populated by GitHub Actions deploy step. Falls back to "local" for dev.
var buildNumber = Environment.GetEnvironmentVariable("BUILD_NUMBER") ?? "local";

app.MapGet("/", () => $"AI Phone — build {buildNumber}");

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger,
    TelemetryClient telemetryClient) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        // Handle system events FIRST (validation doesn't need speed)
        if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                return Results.Ok(new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                });
            }
        }

        // ╔══════════════════════════════════════════════════════════════════════════╗
        // ║  CRITICAL: ANSWER CALL IMMEDIATELY - TOKEN EXPIRES IN ~1-2 SECONDS!      ║
        // ║  NO LOGGING, NO TELEMETRY, NO HASHING BEFORE AnswerCallAsync()!          ║
        // ╚══════════════════════════════════════════════════════════════════════════╝

        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
        var callerId = Helper.GetCallerId(jsonObject);

        // contextId travels through the callback URL AND the WS query string so the
        // /ws handler can correlate the audio stream back to the call connection.
        var inboundContextId = Guid.NewGuid().ToString();
        var callbackUri = new Uri(new Uri(appBaseUrl), $"/api/callbacks/{inboundContextId}?callerId={callerId}");
        var websocketUri = appBaseUrl.Replace("https", "wss") + $"/ws?contextId={inboundContextId}";

        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            MediaStreamingOptions = new MediaStreamingOptions(MediaStreamingAudioChannel.Mixed)
            {
                TransportUri = new Uri(websocketUri),
                MediaStreamingContent = MediaStreamingContent.Audio,
                StartMediaStreaming = true,
                EnableBidirectional = true,
                AudioFormat = AudioFormat.Pcm24KMono
            }
        };

        // Capture start time for latency measurement
        var answerStartTime = DateTime.UtcNow;
        AnswerCallResult answerCallResult;

        try
        {
            // ⚡ ANSWER IMMEDIATELY - This MUST be the first async I/O operation
            answerCallResult = await client.AnswerCallAsync(options);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 400 && ex.Message.Contains("8523"))
        {
            // ErrorCode 8523 = IncomingCallContext expired or invalid
            // Compute diagnostics ONLY after failure (not before answer attempt)
            var ctxLen = incomingCallContext?.Length ?? 0;
            var ctxHash = !string.IsNullOrEmpty(incomingCallContext)
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(incomingCallContext))).Substring(0, 16)
                : "NULL";
            var latencyMs = (DateTime.UtcNow - answerStartTime).TotalMilliseconds;

            telemetryClient.TrackException(ex, new Dictionary<string, string>
            {
                { "Operation", "AnswerCall" },
                { "CallerId", callerId },
                { "ErrorCode", "8523_InvalidContext" },
                { "ContextLength", ctxLen.ToString() },
                { "ContextHash", ctxHash },
                { "AnswerLatencyMs", latencyMs.ToString("F0") },
                { "Status", ex.Status.ToString() },
                { "Message", ex.Message }
            });
            logger.LogError($"[ERROR 8523] Token expired/invalid. Caller={callerId}, ContextLen={ctxLen}, Hash={ctxHash}, LatencyMs={latencyMs:F0}");
            throw;
        }
        catch (Exception ex)
        {
            telemetryClient.TrackException(ex, new Dictionary<string, string>
            {
                { "Operation", "AnswerCall" },
                { "CallerId", callerId }
            });
            logger.LogError(ex, "Error answering call");
            throw;
        }

        // ✅ SUCCESS - Now do all logging/telemetry (call is already connected)
        var answerLatencyMs = (DateTime.UtcNow - answerStartTime).TotalMilliseconds;
        var contextLength = incomingCallContext?.Length ?? 0;
        var connectionId = answerCallResult.CallConnection.CallConnectionId;

        // Store the connection id so the AI's hang_up tool callback can disconnect the PSTN call
        callConnections[inboundContextId] = connectionId;

        logger.LogInformation($"[SUCCESS] Call answered in {answerLatencyMs:F0}ms. CallerId={callerId}, ConnectionId={connectionId}, ContextId={inboundContextId}");

        telemetryClient.TrackEvent("CallAnsweredSuccess", new Dictionary<string, string>
        {
            { "CallConnectionId", connectionId },
            { "CallerId", callerId },
            { "ContextId", inboundContextId },
            { "ContextLength", contextLength.ToString() },
            { "AnswerLatencyMs", answerLatencyMs.ToString("F0") }
        });
    }
    return Results.Ok();
});

// api to handle call back events
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    ILogger<Program> logger,
    TelemetryClient telemetryClient) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation($"Event received: {JsonConvert.SerializeObject(@event, Formatting.Indented)}");

        telemetryClient.TrackEvent("CallAutomationEvent", new Dictionary<string, string>
        {
            { "EventType", @event.GetType().Name },
            { "ContextId", contextId },
            { "CallerId", callerId },
            { "CallConnectionId", @event.CallConnectionId ?? "N/A" }
        });
    }

    return Results.Ok();
});

app.UseWebSockets();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            try
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                var logger = context.RequestServices.GetRequiredService<ILogger<AcsMediaStreamingHandler>>();
                var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();

                var wsContextId = context.Request.Query["contextId"].FirstOrDefault();

                var mediaService = new AcsMediaStreamingHandler(
                    webSocket,
                    builder.Configuration,
                    logger,
                    loggerFactory,
                    aiCredential);

                // Wire the AI's hang_up tool to actually terminate the PSTN call.
                // ACS opens TWO WebSockets per call — use TryRemove so only one fires the
                // HangUpAsync; subsequent attempts find no connectionId and noop.
                if (!string.IsNullOrEmpty(wsContextId))
                {
                    mediaService.OnHangUp(async (reason) =>
                    {
                        if (callConnections.TryRemove(wsContextId, out var connId))
                        {
                            try
                            {
                                logger.LogInformation("Hanging up call {ConnectionId}. Reason: {Reason}", connId, reason);
                                await client.GetCallConnection(connId).HangUpAsync(true);
                                logger.LogInformation("Call {ConnectionId} disconnected successfully", connId);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Failed to hang up call {ConnectionId}", connId);
                            }
                        }
                        else
                        {
                            logger.LogWarning("No call connection found for context {ContextId} — already disconnected?", wsContextId);
                        }
                    });
                }

                await mediaService.ProcessWebSocketAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception received {ex}");
            }
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    else
    {
        await next(context);
    }
});

app.Run();