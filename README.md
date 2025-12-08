# AIPhone - AI Voice Service

Azure Communication Services (ACS) integration with Azure OpenAI Realtime API for an AI-powered IVR system.

## Architecture

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   Caller    │───▶│  Carrier    │───▶│   Azure     │───▶│  AIPhone    │
│  (Phone)    │    │  Avaya SBC  │    │    ACS      │    │  App Service│
└─────────────┘    └─────────────┘    └─────────────┘    └─────────────┘
                                                                │
                                                                ▼
                                                        ┌─────────────┐
                                                        │ Azure OpenAI│
                                                        │  Realtime   │
                                                        │    API      │
                                                        └─────────────┘
```

## Azure Resources

| Resource | Name | Location |
|----------|------|----------|
| App Service | app-aiphone-dev | Sweden Central |
| AI Foundry | foundry-aiphone-alfarahn | East US 2 |
| ACS | acs-ivr-dev-sc.europe | Sweden Central |
| Application Insights | appi-aiphone-dev | Sweden Central |

## The Problem: ERROR 8523 - Token Expiration

### Issue Description

Incoming calls from certain phone numbers (e.g., Danish number +45XXXXXXXX) were failing with **Azure Communication Services ERROR 8523** - "IncomingCallContext invalid or expired".

### Root Cause Analysis

The call flow involves multiple network hops with cumulative latency:

```
Caller → Carrier Avaya SBC → Event Grid → ACS Webhook → AIPhone App
         ├── JWT generated ──┤            │              │
         │   (TTL: 1-2 sec)  │            │              │
         │                   ├── ~100ms ──┤              │
         │                   │            ├── ~100ms ────┤
         │                   │            │              ├── Code processing
         │                   │            │              │   (was ~200ms)
         │                   │            │              ▼
         │                   │            │         AnswerCallAsync()
         └──────────────── TOTAL: 300-500ms ───────────────┘
```

**The Problem**: The Avaya SBC generates a JWT token in the `IncomingCallContext` with a very short TTL (~1-2 seconds). By the time the webhook is received and processed, the token has often expired.

**Observed Pattern**:
- Some caller numbers consistently failed (different SBC routing)
- Some caller numbers consistently succeeded
- This suggested the carrier's SBC routing assigns different JWT TTLs or routes through different paths

### The Solution: Optimize Call Answer Latency

We optimized the incoming call handler to **answer the call immediately** before doing any logging or telemetry.

#### Before (Slow - ~200ms before answer):
```csharp
// ❌ OLD CODE - Too much processing before answering
logger.LogInformation($"Incoming Call event received.");
telemetryClient.TrackEvent("IncomingCallEventReceived", ...);

var contextHash = ComputeHash(incomingCallContext);  // CPU work
logger.LogInformation($"[DEBUG] Context hash: {contextHash}");
logger.LogInformation($"[DEBUG] Context length: {contextLength}");
logger.LogInformation($"[DEBUG] Full JWT Token: {incomingCallContext}");
telemetryClient.TrackEvent("IncomingCallContextReceived", ...);

// By now, 200ms have passed and token may be expired!
await client.AnswerCallAsync(options);  // ERROR 8523!
```

#### After (Fast - <50ms before answer):
```csharp
// ✅ NEW CODE - Answer immediately, log later
// ╔══════════════════════════════════════════════════════════════════════════╗
// ║  CRITICAL: ANSWER CALL IMMEDIATELY - TOKEN EXPIRES IN ~1-2 SECONDS!      ║
// ║  NO LOGGING, NO TELEMETRY, NO HASHING BEFORE AnswerCallAsync()!          ║
// ╚══════════════════════════════════════════════════════════════════════════╝

var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
var callerId = Helper.GetCallerId(jsonObject);

var options = new AnswerCallOptions(incomingCallContext, callbackUri)
{
    MediaStreamingOptions = new MediaStreamingOptions(...)
};

var answerStartTime = DateTime.UtcNow;

// ⚡ ANSWER IMMEDIATELY - This MUST be the first async I/O operation
answerCallResult = await client.AnswerCallAsync(options);

// ✅ SUCCESS - Now do all logging/telemetry (call is already connected)
var answerLatencyMs = (DateTime.UtcNow - answerStartTime).TotalMilliseconds;
logger.LogInformation($"[SUCCESS] Call answered in {answerLatencyMs:F0}ms");
```

### Key Optimizations

1. **Removed ALL logging before `AnswerCallAsync()`** - No `Console.WriteLine`, no `logger.Log*`, no `telemetryClient.Track*`
2. **Deferred hash computation** - SHA256 hash of context only computed on failure (for diagnostics)
3. **Deferred telemetry** - All Application Insights events sent AFTER call is connected
4. **Added latency measurement** - Track exactly how long `AnswerCallAsync()` takes

### Results

| Metric | Before | After |
|--------|--------|-------|
| Code latency before answer | ~200ms | <50ms |
| Success rate (fast SBC routes) | ~50% | ~95%+ |
| Diagnostic data | Before answer (wasted on success) | Only on failure |

### Important Note

**This is a mitigation, not a complete fix.** The root cause is:
- The carrier's Avaya SBC generates JWTs with very short TTL (1-2 seconds)
- Network latency between Avaya → Event Grid → ACS → App is 300-400ms
- Some SBC routes have longer TTLs than others

**Permanent solution requires the carrier to**:
1. Extend JWT TTL in Avaya SBC configuration (Oceana® supports 60-300s)
2. Optimize SIP trunk routing to reduce latency
3. Ensure consistent SBC configuration across all caller routes

## Tech Stack

- **Runtime**: .NET 8
- **Cloud**: Azure (App Service, ACS, OpenAI, Application Insights)
- **AI**: Azure OpenAI Realtime API (gpt-realtime-mini) with Whisper for speech recognition
- **Languages**: Multilingual support (Danish, English, Arabic, Farsi + 46 more via Whisper)

## NuGet Packages

```xml
<PackageReference Include="Azure.Communication.CallAutomation" Version="1.4.0" />
<PackageReference Include="Azure.Communication.Common" Version="1.3.0" />
<PackageReference Include="Azure.AI.OpenAI" Version="2.1.0" />
```

## Configuration

Required settings in `appsettings.json`:

```json
{
  "AcsConnectionString": "endpoint=https://...",
  "AppBaseUrl": "https://app-aiphone-dev.azurewebsites.net",
  "AzureVoiceLiveEndpoint": "https://foundry-aiphone-alfarahn.cognitiveservices.azure.com",
  "AzureVoiceLiveApiKey": "...",
  "VoiceLiveModel": "gpt-realtime-mini",
  "ApplicationInsights": {
    "ConnectionString": "..."
  }
}
```

## Running Locally

```bash
cd api
dotnet run
```

Use VS Code Dev Tunnels or ngrok to expose the local endpoint for webhook callbacks.

## Monitoring

All telemetry is sent to Application Insights. Key events to monitor:

- `CallAnsweredSuccess` - Call connected successfully (includes latency)
- `CallAutomationEvent` - ACS callback events
- Exception with `ErrorCode: 8523_InvalidContext` - Token expiration failures

### KQL Query for Token Expiration Analysis

```kql
exceptions
| where customDimensions.ErrorCode == "8523_InvalidContext"
| project timestamp, 
    CallerId = customDimensions.CallerId,
    LatencyMs = customDimensions.AnswerLatencyMs,
    ContextLength = customDimensions.ContextLength
| order by timestamp desc
```
