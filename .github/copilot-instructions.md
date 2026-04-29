NEVER UPDATE OTHER FILES - ONLY THIS FILE (.github/copilot-instructions.md)
NEVER CREATE: documentation, README, scripts
NEVER BUILD OR DEPLOY LOCALLY (no `dotnet build`, no `az acr build`, no `az containerapp update`) — all builds and deployments happen via the GitHub Actions workflow `.github/workflows/main_ai-phone.yml`. Push to main to trigger.
Repo: https://github.com/AliRezaFarahnak-Customer-Demos/AIPhone

## CURRENT STATUS: Stable

**Test Phone Number**: +4580719050 (Danish)
**Note**: Previously had ERROR 8523 (token expiration) with old Avaya SBC carrier — resolved after carrier change. Code still optimized for fast call answering (<50ms before AnswerCallAsync).

## Azure Resources

**Subscription**: ME-MngEnv986490-alfarahn-1
**Resource Group**: rg-myagents

| Resource               | Name              | Location       | Details                                          |
| ---------------------- | ----------------- | -------------- | ------------------------------------------------ |
| Container App (caller) | ca-caller-agent   | Sweden Central | crmyagents.azurecr.io/caller-agent               |
| Container App (admin)  | ca-admin-chat     | Sweden Central | Custom domain: ai-call-center.app                |
| AI Services            | cog-myagents      | Sweden Central | https://cog-myagents.cognitiveservices.azure.com |
| ACS                    | acs-myagents      | Global         |                                                  |
| AppInsights            | appi-myagents     | Sweden Central |                                                  |
| Container Registry     | crmyagents        | Sweden Central |                                                  |
| Container App Env      | cae-myagents      | Sweden Central |                                                  |
| Event Grid             | evgt-myagents-acs | Global         |                                                  |
| Log Analytics          | log-myagents      | Sweden Central |                                                  |
| Storage                | stmyagents        | Sweden Central |                                                  |

## Model Deployments (cog-myagents)

| Deployment   | Model            | Version    |
| ------------ | ---------------- | ---------- |
| gpt-realtime | gpt-realtime-1.5 | 2026-02-23 |
| gpt-5.2      | gpt-5.2          | 2025-12-11 |
| gpt-5.4-pro  | gpt-5.4-pro      | 2026-03-05 |

## Custom Domains

- https://ai-call-center.app → ca-admin-chat
- https://www.ai-call-center.app → ca-admin-chat

## Key NuGet Versions

- Azure.Communication.CallAutomation v1.4.0
- Azure.Communication.Common v1.3.0
- Azure.AI.OpenAI v2.1.0-beta.2

## Code Status

- Program.cs: ✅ Optimized (answer first, log second, <50ms response)
- Helper.cs: ✅ Caller ID + context validation working
- AzureVoiceLiveService.cs: ✅ Nordic Bank assistant (Danish/English)

## Reference Implementation

**Source of truth**: `c:\repos\ContactCenterAgent\code\caller-agent\agent\` (CCA). AIPhone is a stripped clone — wire-payload identical for AI/audio behavior. Only intentional deltas:

| Field               | AIPhone                                 | CCA                                 |
| ------------------- | --------------------------------------- | ----------------------------------- |
| `voice.name`        | `en-US-Andrew:DragonHDOmniLatestNeural` | per-call (Norlys default)           |
| `voice.style`       | `shouting`                              | `friendly`                          |
| `voice.locale`      | omitted (Omni multilingual auto-detect) | `da-DK` pinned                      |
| `voice.temperature` | `0.9`                                   | `0.9`                               |
| System prompt       | hardcoded Andrew shouting, opens "HEJ!" | per-call from admin-chat            |
| Farewell text       | "SHOUT in caller's language"            | "Tak fordi du er kunde hos Norlys…" |
| `phrase_list`       | empty (no domain vocab)                 | NorlysDanishPhrases                 |
| Transcription model | `azure-speech` (same default as CCA)    | `azure-speech`                      |

**Not ported** (CCA-only features, deliberately omitted): per-call prompt/language API, ConversationAnalysisService, SSE transcript/analysis channels, AppInsights `TelemetryClient` event tagging, outbound `/api/outboundCall` endpoint.

**Always identical to CCA** (do not drift): VAD knobs (azure_semantic_vad / 0.3 / 300ms / 500ms), `interrupt_response=false` + `create_response=false` greeting gating, `azure_deep_noise_suppression`, `server_echo_cancellation`, pcm16 24kHz both ways, hang_up tool schema + farewell sequencing, 401 retry with `claims="{}"`, contextId WS query → `callConnections` map → `HangUpAsync(true)`.
