# AIPhone - AI Voice Service

Azure Communication Services (ACS) integration with Azure OpenAI Realtime API for an AI-powered IVR system.

## Architecture

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   Caller    │───▶│  Carrier    │───▶│   Azure     │───▶│  AIPhone    │
│  (Phone)    │    │   (PSTN)    │    │    ACS      │    │ Container App│
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

**Subscription**: ME-MngEnv986490-alfarahn-1  
**Resource Group**: rg-myagents

| Resource               | Name              | Location       |
| ---------------------- | ----------------- | -------------- |
| Container App (caller) | ca-caller-agent   | Sweden Central |
| Container App (admin)  | ca-admin-chat     | Sweden Central |
| AI Services            | cog-myagents      | Sweden Central |
| ACS                    | acs-myagents      | Global         |
| Application Insights   | appi-myagents     | Sweden Central |
| Container Registry     | crmyagents        | Sweden Central |
| Container App Env      | cae-myagents      | Sweden Central |
| Event Grid             | evgt-myagents-acs | Global         |
| Log Analytics          | log-myagents      | Sweden Central |
| Storage                | stmyagents        | Sweden Central |

## Tech Stack

- **Runtime**: .NET 8
- **Cloud**: Azure (Container Apps, ACS, OpenAI, Application Insights)
- **AI**: Azure OpenAI Realtime API (gpt-realtime-1.5) with Whisper for speech recognition
- **Languages**: Multilingual support (Danish, English, Arabic, Farsi + 46 more via Whisper)

## NuGet Packages

```xml
<PackageReference Include="Azure.Communication.CallAutomation" Version="1.4.0" />
<PackageReference Include="Azure.Communication.Common" Version="1.3.0" />
<PackageReference Include="Azure.AI.OpenAI" Version="2.1.0-beta.2" />
```

## Configuration

Required settings in `appsettings.json`:

```json
{
  "AcsConnectionString": "endpoint=https://...",
  "AppBaseUrl": "https://ca-caller-agent.happycliff-98433b2c.swedencentral.azurecontainerapps.io",
  "AzureVoiceLiveEndpoint": "https://cog-myagents.cognitiveservices.azure.com",
  "AzureVoiceLiveApiKey": "...",
  "VoiceLiveModel": "gpt-realtime-1.5",
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
