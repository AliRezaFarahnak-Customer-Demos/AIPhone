NEVER UPDATE OTHER FILES - ONLY THIS FILE (.github/copilot-instructions.md)
NEVER CREATE: documentation, README, scripts
Repo: https://github.com/AliRezaFarahnak-Customer-Demos/AIPhone

## CURRENT STATUS: ERROR 8523 - Token Expiration (RESOLVED)
**Issue**: IncomingCallContext JWT expires during network transit (372ms delay observed)
**Root Cause**: Avaya SBC generates JWT with 1-2s TTL. Network latency (Avaya→Event Grid→ACS) takes 300-400ms. Token expires before ACS validates it.
**Status**: MITIGATED - Code optimized to answer calls immediately (<50ms), reducing token expiration failures
**Solution Applied**: Answer first, log second pattern - all logging/telemetry moved AFTER AnswerCallAsync()
**Test Phone Number**: +4580719050 (Danish)

## Recent Diagnostics (Dec 8, 2025)
- Code optimized: removed all logging before AnswerCallAsync()
- Latency reduced from ~200ms to <50ms before answer attempt
- Telemetry now tracks answer latency for monitoring
- Pattern: Some SBC routes have shorter JWT TTLs than others (carrier configuration issue)

## Azure Resources
| Resource | Location | Details |
|----------|----------|---------|
| App Service | app-aiphone-dev | Sweden Central |
| AI Foundry | foundry-aiphone-alfarahn | East US 2 (gpt-realtime-mini) |
| ACS | acs-ivr-dev-sc.europe | Sweden Central |
| AppInsights | appi-aiphone-dev | Sweden Central |

## Key NuGet Versions (STABLE ONLY)
- Azure.Communication.CallAutomation v1.4.0
- Azure.Communication.Common v1.3.0

## Code Status
- Program.cs: ✅ Optimized (answer first, log second, <50ms response)
- Helper.cs: ✅ Caller ID + context validation working
- AzureVoiceLiveService.cs: ✅ Nordic Bank assistant (Danish/English)

## Avaya Documentation Found
JWT TTL is configurable in Avaya (60-300s range documented in Oceana®), but SBC IncomingCallContext uses ~1-2s (NOT documented as configurable)
