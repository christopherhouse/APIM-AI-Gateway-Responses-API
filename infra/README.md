# Infrastructure and gateway configuration - tasks 002/003/004

This infrastructure was **deployed and exercised through azd in task `007`**;
the separate clean CLI lifecycle remains in progress. It creates
resources inside an existing dedicated resource group; it never creates or
selects that group. No Azure deployment or live validation was performed for
tasks `002`-`004`. Task `004` adds the external group lifecycle in
[`scripts/`](../scripts/README.md). The disposable task `001` experiment is separate.

## Resources and module pins

| Resource | Exact AVM Bicep module | Configuration |
| --- | --- | --- |
| Gateway | `avm/res/api-management/service:0.15.0` | Developer, one unit; dedicated user-assigned identity only; public endpoint; no VNet |
| Gateway identity | `avm/res/managed-identity/user-assigned-identity:0.6.0` | New owned identity in the same group/region; no federated credentials or extra role grants |
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

[`gateway.bicep`](gateway.bicep) adds APIM child resources after the service,
both model deployments, and identity grants are ready. It uses
`2025-03-01-preview` for native backend affinity; the pinned APIM AVM backend
child uses `2024-05-01` and cannot express `sessionAffinity`. API, operation,
policy, subscription, logger, and API diagnostic children retain `2024-05-01`.
Keeping them in a separate module also avoids a service/identity/account
dependency cycle; it does not create a second APIM service. See the
[gateway contract](policies/README.md) for request modes, error handling,
evidence headers, native traces, and live-verification limits.

All regional resources are fixed to `westus3`. Model inference can run globally.
APIM's dedicated user-assigned identity receives only **Cognitive Services OpenAI User** at each
of the two account scopes. The template does not grant operator, harness, or
evidence-reader permissions, and it does not retrieve gateway subscription keys.
The two grants are direct account-scoped extension resources: a cold Provider
what-if rejected the AVM account module's non-null `roleAssignments` parameter
when the new APIM principal was not yet known. Keeping the runtime reference
on the actual grant preserves the role, scopes, and predeployment validation.
The user subsequently required the dedicated user-assigned identity; the same
runtime-principal constraint applies to its cold deployment. APIM has no
system-assigned identity. The policy explicitly selects the user-assigned
identity's client ID rather than relying on a default credential.

## Native deployment parameters

Maintain deployment inputs as **`.bicepparam` files**, not JSON parameter files.
[`main.bicepparam`](main.bicepparam) uses the entry point and contains placeholder
publisher metadata only. The supported lifecycle drivers both use
[`deploy.bicepparam`](deploy.bicepparam): it targets the same Bicep and reads
local non-secret metadata/model inputs from the process environment.
The wrapper fixes target/region/ownership inputs and checks an immutable
compiled-input hash. For direct lower-level experimentation only, a copy of
the placeholder file can live at ignored `infra\main.local.bicepparam`;
it is not consumed by the wrappers. Never put credentials in parameters.

| Input | Default / constraint |
| --- | --- |
| `environmentName` | Required; 2-16 lowercase letters/digits/interior hyphens, starting/ending alphanumeric |
| `publisherName`, `publisherEmail` | Required local operator metadata; replace the example placeholders |
| `location` | `westus3` only; no fallback region |
| `apimName`, `foundryAName`, `foundryBName`, `workspaceName` | Deterministic environment/group-based defaults; optional explicit names |
| `modelName`, `modelVersion`, `modelDeploymentName` | `gpt-5.4-mini`, `2026-03-17`, `responses-chat` |
| `affinityCookieName` | Fixed `APIM-Backend-Affinity`; exported name only, never an opaque value |
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
rerun; incremental deployment does not remove the old resources. The task `004`
wrappers reject changed inputs/targets and verify the externally
created group's ownership tags and parent inventory before reuse or deletion.
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
The new user-assigned identity is included in the task `007` live diagnostic
capability inventory; do not assume it can emit diagnostic settings.
Activity Log changes are separate; this RG-scoped template does not export
subscription Activity Logs or create a workspace data-export pipeline.

Use **synthetic, non-sensitive traffic only**. Account `RequestResponse`/`Trace`
categories and workspace query audits can contain service/query metadata;
enabling them is not a claim that all records are content-free. This template
does not enable any additional content-capture feature. Review emitted fields
and table-level retention in task `007` before sharing evidence. The workspace
default is 30 days; service/table retention rules can differ.

Task `003` configures the API's explicit `azuremonitor` logger and 100% fixed
sampling at `information` verbosity, all errors, no client IP, and zero body
bytes in both directions. Header capture is empty except gateway-written
request/member response IDs. Compact `responses-lab` traces exclude payloads,
credentials, cookies, and raw URLs. Resource diagnostics plus this API
configuration select the native `TraceRecords` path. Task `007` correlated all
32 first-environment requests with actual native traces and verified no APIM
body, credential/cookie-header, or client-IP capture in those records.
No Application Insights/extra collector is added.

Task `007` also compared live categories/settings for all four parents and
checked 13 identity/child/control-plane types: no additional independent
categories were supported. Foundry request/usage/audit records and workspace
`LAQueryLogs` were emitted; quiet workloads were not fabricated.
The workspace and gateway/diagnostic/metric/query-audit tables retained
30 days, while the service's `Usage` table retained 90 days. Query audits can
identify operators and queries; raw records are not safe publication samples.

Azure Policy added other Foundry diagnostic settings targeting a central
workspace. The lab did not modify them or query that destination. Its privacy
and retention are governed separately; deleting the lab workspace does not
delete externally retained records. See the bounded, sanitized
[task `007` evidence](../evidence/samples/task007-verification.json).

## Non-secret output contract

The entry point returns `resourceGroupName`, `apimResourceId`, `apimGatewayUrl`,
`apimPrincipalId` (the attached user-assigned identity's principal),
`apimIdentityResourceId`, `foundryAResourceId`, `foundryBResourceId`,
`foundryAEndpoint`, `foundryBEndpoint`, `workspaceResourceId`,
`workspaceCustomerId`, and `modelDeploymentName`. URLs are read from actual
service properties, not assembled from assumed DNS suffixes. Explicit module
dependencies prevent reading those properties before provisioning finishes.

Task `003` adds `responsesEndpoint`, `backendAId`, `backendBId`,
`apimSubscriptionResourceId`, and `affinityCookieName` from the implemented
gateway resources. These are deployment outputs, not a claim of a live
endpoint. `lab-access` is scoped to this API, active with tracing disabled;
Azure generates its keys, and no template output retrieves or exposes them.

## Local validation (no Azure sign-in required)

From the repository root, use Azure CLI with Bicep installed, Node.js 24, and
PowerShell 7 (`pwsh`, including its bundled Newtonsoft for expression tests).
The verified local Bicep version is `0.47.16`. Initial compilation downloads
the exact public AVM modules into the local Bicep cache; it creates no Azure
resources. Lifecycle checks also need azd 1.36.0+ installed for executable
discovery, but mock its commands. Keep generated JSON compiler output under
ignored `artifacts`.

```powershell
az bicep build --file .\infra\main.bicep --outfile .\artifacts\002-template.json
az bicep build-params --file .\infra\main.bicepparam --outfile .\artifacts\002-parameters.json
node --test .\infra\foundation.test.mjs .\infra\gateway.test.mjs
pwsh -NoProfile -File .\infra\policy.test.ps1
pwsh -NoProfile -File .\scripts\lifecycle.test.ps1
git diff --check
```

Build performs Bicep syntax/type checking and linting. `build-params` validates
the native parameter file against its `using` target. The artifact names retain
the foundation test contract and contain the current complete entry point.
Eleven Node tests check foundation/routing resource wiring, native affinity/
breaker configuration, scoped auth, diagnostic privacy, output safety,
dependency ordering, and parameter coverage.

The PowerShell test parses XML, compiles all 19 actual policy expressions as
C# 7 against small context stand-ins, and executes 42 input/host fixtures.
It uses no live gateway and installs no dependencies. Its host references
PowerShell's bundled Newtonsoft; the specific assembly-version compatibility
warning is suppressed without suppressing compilation errors.
These are local structural/expression checks, **not an APIM runtime emulator,
ARM expression interpreter, or provider-side validation**.

ARM `validate`/`what-if` were not run under tasks `002`-`004`'s code-only
authorization. Task `007` subsequently passed actual Provider validation and
what-if before the first deployment and its unchanged rerun.
Before any future deployment,
obtain authorization and use the actual dedicated group and local
`.bicepparam` file for ARM validation and resource-level what-if review.
Do not treat the task `001` ARM results as validation of these new modules.

Task `007` records actual provisioning, RBAC propagation, public endpoint
authentication, all supported diagnostic settings, event/table emission,
privacy/retention, model availability and capacity, output endpoint properties,
and the exact expanded policy's routing/error behavior and trace ingestion.
Task `004` implements azd/CLI lifecycle orchestration using this entry point;
its offline checks do not establish real provisioning or teardown.
Task `005` adds the [offline-tested .NET harness](../src/ResponsesLab/README.md).
Task `006` adds guarded fault/recovery automation with offline checks only.
The first environment passed healthy routing, real backend failure, native
breaker error correlation, and original-chain recovery. Exhaustive invalid-input
rejection paths were not sent live; their local fixtures are not live evidence.
Do not count the old experiment as verification of the new templates.

## Official implementation sources (inspected 2026-10-15)

- [APIM module 0.15.0](https://github.com/Azure/bicep-registry-modules/blob/avm/res/api-management/service/0.15.0/avm/res/api-management/service/main.bicep)
- [APIM module backend child and its stable-schema limitation](https://github.com/Azure/bicep-registry-modules/blob/avm/res/api-management/service/0.15.0/avm/res/api-management/service/backend/main.bicep)
- [Foundry account module 0.13.2](https://github.com/Azure/bicep-registry-modules/blob/avm/res/cognitive-services/account/0.13.2/avm/res/cognitive-services/account/main.bicep)
- [Workspace module 0.16.1, including secure outputs](https://github.com/Azure/bicep-registry-modules/blob/avm/res/operational-insights/workspace/0.16.1/avm/res/operational-insights/workspace/main.bicep)
- [Bicep best practices](https://learn.microsoft.com/azure/azure-resource-manager/bicep/best-practices)
- [Native Bicep parameter files](https://learn.microsoft.com/azure/azure-resource-manager/bicep/parameter-files)
- [Azure resource naming rules](https://learn.microsoft.com/azure/azure-resource-manager/management/resource-name-rules)
- [Established design, diagnostics inventory, and live feasibility sources](../docs/DESIGN.md)
