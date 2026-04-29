using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using CallAutomation.AzureAI.VoiceLive;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;

var builder = WebApplication.CreateBuilder(args);

// Add local settings for development (file is in .gitignore)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Add Application Insights telemetry
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
});

//Get ACS Connection String from appsettings.json
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

//Call Automation Client
var client = new CallAutomationClient(acsConnectionString);
var app = builder.Build();

// Get app base URL from multiple sources (priority order)
var appBaseUrl = Environment.GetEnvironmentVariable("VS_TUNNEL_URL")?.TrimEnd('/');

if (string.IsNullOrEmpty(appBaseUrl))
{
    appBaseUrl = builder.Configuration.GetValue<string>("AppBaseUrl");
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

app.MapGet("/", () => "Hello ACS CallAutomation!");

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

        // Build options with MINIMAL processing - no logging, no hashing, no telemetry
        var callbackUri = new Uri(new Uri(appBaseUrl), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
        var websocketUri = appBaseUrl.Replace("https", "wss") + "/ws";

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

        logger.LogInformation($"[SUCCESS] Call answered in {answerLatencyMs:F0}ms. CallerId={callerId}, ConnectionId={answerCallResult.CallConnection.CallConnectionId}");

        telemetryClient.TrackEvent("CallAnsweredSuccess", new Dictionary<string, string>
        {
            { "CallConnectionId", answerCallResult.CallConnection.CallConnectionId },
            { "CallerId", callerId },
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

                var mediaService = new AcsMediaStreamingHandler(
                    webSocket,
                    builder.Configuration,
                    logger,
                    loggerFactory);

                // Set the single WebSocket connection
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