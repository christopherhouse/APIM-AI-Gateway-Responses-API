# Infrastructure foundation - task 002

This is infrastructure code, **not a deployed or callable lab**. It creates
resources inside an existing dedicated resource group; it never creates or
selects that group. No Azure deployment or live validation was performed for
task `002`. The disposable task `001` experiment is separate.

## Resources and module pins

| Resource | Exact AVM Bicep module | Configuration |
| --- | --- | --- |
| Gateway | `avm/res/api-management/service:0.15.0` | Developer, one unit; system-assigned identity; public endpoint; no VNet |
| Two Foundry accounts | `avm/res/cognitive-services/account:0.13.2` | `AIServices` / `S0`; separate custom subdomains; public endpoints; local/key authentication disabled |
| Model in each account | Same account module | `gpt-5.4-mini` / `2026-03-17`; `GlobalStandard`, capacity 20 each; `NoAutoUpgrade` |
| Log Analytics | `avm/res/operational-insights/workspace:0.16.1` | `PerGB2018`; 30-day default retention; 1 GB/day ingestion quota; public query/ingestion; local authentication disabled |

Module versions are intentional pins, not floating references.
The pattern-module review rejected `azd/apim-api` (existing gateway, anonymous
API, different logging defaults), `ai-ml/ai-foundry` (unneeded project/agent
resources), and `azd/monitoring` (unneeded Application Insights). Resource AVMs
fit the approved minimal architecture without those dependencies.

APIM and Foundry use the task `001` control-plane APIs:
`2024-05-01` and `2025-06-01`. The workspace module uses **`2025-07-01`** instead
of the probe's `2023-09-01`. This deliberate change avoids the older workspace
module's ordinary shared-key outputs. The selected AVM marks its nested key
outputs secure; the entry point exposes no keys and does not re-export them.
Diagnostic settings use `2021-05-01-preview`; role assignments use `2022-04-01`.

All regional resources are fixed to `westus3`. Model inference can run globally.
APIM's system identity receives only **Cognitive Services OpenAI User** at each
of the two account scopes. The template does not grant operator, harness, or
evidence-reader permissions, and it does not retrieve gateway subscription keys.

## Native deployment parameters

Maintain deployment inputs as **`.bicepparam` files**, not JSON parameter files.
[`main.bicepparam`](main.bicepparam) uses the entry point and contains placeholder
publisher metadata only. For a future authorized deployment, copy it to ignored
`infra\main.local.bicepparam` and supply real operator metadata locally.
Never add credentials to either file.

| Input | Default / constraint |
| --- | --- |
| `environmentName` | Required; 2-16 lowercase letters/digits/interior hyphens, starting/ending alphanumeric |
| `publisherName`, `publisherEmail` | Required local operator metadata; replace the example placeholders |
| `location` | `westus3` only; no fallback region |
| `apimName`, `foundryAName`, `foundryBName`, `workspaceName` | Deterministic environment/group-based defaults; optional explicit names |
| `modelName`, `modelVersion`, `modelDeploymentName` | `gpt-5.4-mini`, `2026-03-17`, `responses-chat` |
| `modelDeploymentSku`, `modelCapacity` | `GlobalStandard`; 1-20 per account, default 20 |
| `logRetentionDays`, `logDailyQuotaGb` | 30-90 days, default 30; 1-5 GB/day, default 1 |
| `tags` | Additional approved tags; required `azd-env-name` and `purpose` ownership values win |

Explicit APIM/Foundry names must be globally available and use Azure-compatible
letters, digits, and interior hyphens. The two Foundry names must differ.
Workspace names must be 4-63 characters and start/end alphanumeric. Bicep
enforces lengths, types, region, SKU, and numeric bounds; complete naming
syntax, uniqueness, publisher-email validity, ownership, permissions, provider
support, policy, model lifecycle, minimum capacity, and quota still need the
task `004` preflight and Azure validation. Only capacity **20** has the recorded
live feasibility evidence; the allowed smaller values are not availability
guarantees.

Resource names are stable for the same group and environment. Changing the
environment or explicit names can create additional billable resources on a
rerun; incremental deployment does not remove the old resources. The externally
created group's ownership tags must be checked by the later orchestration.
Workspace caps can overshoot and are not billing caps.

## Diagnostics inventory and privacy

[`diagnostic-categories.json`](diagnostic-categories.json) is a category
inventory consumed by Bicep, **not a deployment parameter file**. It contains
the categories discovered in the authorized task `001` deployment.

| Resource | Enabled log categories | Metrics / destination |
| --- | --- | --- |
| APIM | `GatewayLogs`, `GatewayLlmLogs`, `GatewayMCPLogs`, `DeveloperPortalAuditLogs`, `WebSocketConnectionLogs` | `AllMetrics`; resource-specific (`Dedicated`) |
| Each Foundry account | `Audit`, `AzureOpenAIRequestUsage`, `ManagedNetworkEvent`, `RequestResponse`, `Trace` | `AllMetrics`; `AzureDiagnostics` |
| Workspace itself | `Audit`, `Jobs`, `SummaryLogs` | `AllMetrics`; resource-specific (`Dedicated`), same workspace |

Every listed category is enabled; neither quiet categories nor absence of
traffic establishes ingestion. At deployment/verification time, query each
parent's diagnostic-category endpoint and compare it with this inventory.
Fail on additions, removals, or non-exportable metric differences rather than
silently dropping categories. Independently check the child-resource inventory.
No fresh category discovery is claimed for task `002`.

**Not independently configured:** model deployments, role assignments,
diagnostic settings, nested ARM deployment records, and the externally managed
group have no independently established resource log categories. Future APIM
APIs/backends/policies/subscriptions share their parent's resource diagnostics.
Activity Log changes are separate; this RG-scoped template does not export
subscription Activity Logs or create a workspace data-export pipeline.

Use **synthetic, non-sensitive traffic only**. Account `RequestResponse`/`Trace`
categories and workspace query audits can contain service/query metadata;
enabling them is not a claim that all records are content-free. This template
does not enable any additional content-capture feature. Review emitted fields
and table-level retention in task `007` before sharing evidence. The workspace
default is 30 days; service/table retention rules can differ.

Gateway body/header/IP capture controls and compact policy traces belong to
task `003`. The foundation intentionally creates no API/service diagnostic
sampling configuration, `azuremonitor` logger, proxy, or policy. Until task
`003`, **the selected policy-trace path is not configured**. Resource diagnostic
settings alone do not produce `TraceRecords` or a callable Responses endpoint.

## Non-secret output contract

The entry point returns `resourceGroupName`, `apimResourceId`, `apimGatewayUrl`,
`apimPrincipalId`, `foundryAResourceId`, `foundryBResourceId`,
`foundryAEndpoint`, `foundryBEndpoint`, `workspaceResourceId`,
`workspaceCustomerId`, and `modelDeploymentName`. URLs are read from actual
service properties, not assembled from assumed DNS suffixes. Explicit module
dependencies prevent reading those properties before provisioning finishes.

`responsesEndpoint`, `backendAId`, `backendBId`, and
`apimSubscriptionResourceId` remain task `003` outputs: those resources do not
exist yet. No placeholder identifier is presented as a usable resource.

## Local validation (no Azure sign-in required)

From the repository root, use Azure CLI with Bicep installed and Node.js 24.
The verified local Bicep version is `0.47.16`. Initial compilation downloads
the exact public AVM modules into the local Bicep cache; it creates no Azure
resources. Keep the generated JSON compiler output under ignored `artifacts`.

```powershell
az bicep build --file .\infra\main.bicep --outfile .\artifacts\002-template.json
az bicep build-params --file .\infra\main.bicepparam --outfile .\artifacts\002-parameters.json
node --test .\infra\foundation.test.mjs
git diff --check
```

Build performs Bicep syntax/type checking and linting. `build-params` validates
the native parameter file against its `using` target. The six offline tests
check compiled scope and task boundaries, identical model wiring, scoped RBAC,
Developer/identity settings, complete inventory wiring, output safety/dependency
ordering, and parameter coverage. They are structural checks, **not an ARM
expression interpreter or provider-side validation**.

ARM `validate`/`what-if` are not run: task `002` has no authorization for live
subscription checks and no dedicated deployed group. Do not create a group to
run them under this task's code-only approval. Before any future deployment,
obtain authorization and use the actual dedicated group and local
`.bicepparam` file for ARM validation and resource-level what-if review.
Do not treat the task `001` ARM results as validation of these new modules.

Task `007` must verify actual provisioning, RBAC propagation, public endpoint
authentication, all supported diagnostic settings, event/table emission,
privacy/retention, model availability and capacity, output endpoint properties,
and policy traces after task `003`. `azd`, CLI lifecycle orchestration, runtime
policies, and the .NET harness are not implemented here.

## Official implementation sources (inspected 2026-10-15)

- [APIM module 0.15.0](https://github.com/Azure/bicep-registry-modules/blob/avm/res/api-management/service/0.15.0/avm/res/api-management/service/main.bicep)
- [Foundry account module 0.13.2](https://github.com/Azure/bicep-registry-modules/blob/avm/res/cognitive-services/account/0.13.2/avm/res/cognitive-services/account/main.bicep)
- [Workspace module 0.16.1, including secure outputs](https://github.com/Azure/bicep-registry-modules/blob/avm/res/operational-insights/workspace/0.16.1/avm/res/operational-insights/workspace/main.bicep)
- [Bicep best practices](https://learn.microsoft.com/azure/azure-resource-manager/bicep/best-practices)
- [Native Bicep parameter files](https://learn.microsoft.com/azure/azure-resource-manager/bicep/parameter-files)
- [Azure resource naming rules](https://learn.microsoft.com/azure/azure-resource-manager/management/resource-name-rules)
- [Established design, diagnostics inventory, and live feasibility sources](../docs/DESIGN.md)
