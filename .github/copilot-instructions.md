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
