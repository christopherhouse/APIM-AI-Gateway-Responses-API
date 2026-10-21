# Run the affinity comparison

**A response ID is not a routing instruction.** Foundry owns stored response
state; APIM chooses the backend. This walkthrough first disables the native
pool pin, observes a real broken cross-backend chain, then enables the pin
and checks successful conversations. No history is replayed or copied between
accounts, and no model-generated explanation counts as routing evidence.

**Live-verified on October 16, 2026.** A new lab passed the real off/on
comparison, all 42 request/trace joins including diagnostics, and an unchanged
rerun preserving 13 runtime values. The owned group is verified absent, local
state retired, and recovery records retained without purge. See the
[sanitized results](../evidence/samples/task008-verification.json) and
[actual console examples](../evidence/samples/task008-console.md).
Earlier [task `007` results](../evidence/samples/task007-verification.json)
remain the separate two-driver failure/recovery baseline.
These commands use the default **raw HTTP control**. Task `009` adds
[Foundry SDK and Agent Framework selectors](../README.md#-three-clients-the-same-state-and-routing-rules)
and separately verified their off/on behavior on October 19, 2026: four real
cross-backend state failures and 24 successful affinity-on requests per mode,
using explicitly configured per-conversation transports. Both stock-cookie
profiles exposed shared-state contamination rather than session isolation.
All 110 attempts, including the raw control and stock-profile rejections,
matched actual gateway policy traces. See the
[separate live evidence](../evidence/samples/task009-live-verification.json);
it does not expand the earlier walkthrough's rerun or two-driver proof.
Every new execution still requires its own target/action and aggregate-budget
authorization; choosing another selector does not grant additional attempts.

## 1. Prepare a new lab

Use a Windows terminal running **PowerShell 7** (`pwsh`). Install Git, the
.NET 10 SDK, Azure CLI 2.83.0+ with Bicep, and optionally azd 1.36.0+.
The [lifecycle prerequisites](../scripts/README.md#prerequisites-and-target)
describe provider registration, model availability/quota, operator permissions,
and supported isolated azd-to-CLI authentication.

```powershell
git clone https://github.com/christopherhouse/APIM-AI-Gateway-Responses-API.git
Set-Location .\APIM-AI-Gateway-Responses-API
az login
az account show --query '{name:name,id:id,tenant:tenantId}' --output table
```

Do not proceed until the displayed subscription is the one you intend to
use. Nothing below switches it or elevates permissions. For the azd driver,
also sign in with `azd auth login` as the same operator and tenant.
Use synthetic prompts only and obtain explicit authorization for your exact
target, real inference, affinity updates and deletion.

```powershell
$subscription = 'REPLACE-WITH-THE-ACTIVE-SUBSCRIPTION-UUID'
$environment = 'affinitydemo' # 2-16 lowercase letters/digits/interior hyphens
$group = "rg-responses-$environment"
$driver = 'Azd' # Set 'Cli' for the complete Azure CLI-only path; no azd needed.
$env:AZURE_LOCATION = 'westus3'
$env:LAB_PUBLISHER_NAME = 'REPLACE-WITH-YOUR-PUBLISHER-NAME'
$env:LAB_PUBLISHER_EMAIL = 'REPLACE-WITH-YOUR-EMAIL'
$env:LAB_SESSION_AFFINITY_ENABLED = 'false'
$env:LAB_MAX_OUTPUT_TOKENS = '2048'
$env:LAB_MAX_RETRIES = '0'
```

Use a **new** group and environment. The wrappers create the group outside
Bicep and record a random ownership marker locally. Do not reuse someone
else's empty group, copy ownership files, or edit saved state to force reuse.
All regional resources are in West US 3; GlobalStandard inference can execute
globally. Developer APIM is a lab tier, not a production SLA.

## 2. Build the client and provision

```powershell
dotnet build .\ResponsesLab.slnx --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet test .\ResponsesLab.slnx --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
dotnet publish .\src\ResponsesLab\ResponsesLab.csproj --configuration Release --no-restore --output .\artifacts\publish\ResponsesLab
if ($LASTEXITCODE -ne 0) { throw 'Client publishing failed.' }
$client = (Resolve-Path .\artifacts\publish\ResponsesLab\ResponsesLab.exe).Path
& $client --help

& .\scripts\Invoke-Lab.ps1 -Action Preflight -Driver $driver -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure
& .\scripts\Invoke-Lab.ps1 -Action Provision -Driver $driver -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowDeploy
```

Both driver values use the **same** Bicep and native `.bicepparam` inputs.
For `Cli`, the wrapper invokes `az deployment group create`; for `Azd`, it
initializes a bound environment and invokes `azd provision`. Both run
preflight, Provider validation and what-if. Do not use bare `azd up` or
maintain separate JSON parameter source files.

Provisioning creates one Developer gateway, one dedicated user-assigned
identity, two identical model deployments in separate Foundry accounts,
two account-scoped identity grants, and one workspace. APIM has no
system-assigned identity; its policy explicitly selects the dedicated identity.
Caller access uses an API-scoped subscription key, while backend access uses
managed identity. Public endpoints are not anonymous endpoints.

APIM can take tens of minutes to provision. Keep the command attached; do
not start another operation on the same environment. A nonzero process exit
is not success. For authentication/polling failures, inspect the exact latest
deployment and reconcile using the [troubleshooting guide](../scripts/README.md#offline-checks-and-live-limits);
never export stale successful outputs over a failed/newer deployment.

## 3. Affinity OFF: demonstrate the broken chain

The Bicep switch is **`sessionAffinityEnabled`**. The wrapper reads
**`LAB_SESSION_AFFINITY_ENABLED`**, accepts lowercase `true`/`false`, and
exports the actual deployment mode to the client. In off mode, Bicep omits
the pool's `sessionAffinity` property; the policy permits a chained ID
without a cookie. Authentication, evidence validation, weights, and breakers
remain enabled. This is not a client-only cookie suppression experiment.

```powershell
$runtime = Get-Content ".\artifacts\lab\$environment\runtime.json" -Raw | ConvertFrom-Json
if ($runtime.LAB_SESSION_AFFINITY_ENABLED -ne 'false') { throw 'Deploy affinity off first.' }
& .\scripts\Invoke-Lab.ps1 -Action Run -Driver $driver -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowCredentialRead -ClientPath $client -ClientArguments @('--allow-requests', '--scenario', 'affinity-off')
```

The wrapper retrieves the key into the child-process environment only.
The off-mode client establishes up to four independent chains, then sends
their stored `previous_response_id` values without cookies. Defaults allow
at most **12 HTTP attempts**; each broken chain stops, without retry,
restart, forced routing, or history replay.

**Success means observing a real failure:** at least one chained request
must reach the *other* gateway-verified member and receive HTTP 400/404 with
a recognized response-state error, a valid gateway request UUID, and the
preceding successful response ID. The verified service returned `code=null`,
`param=null`, `type=invalid_request_error` and the exact different-resource
message listed in the [harness contract](../src/ResponsesLab/README.md#explicit-affinity-off-comparison).
The alternative explicit `previous_response_not_found` code is also recognized;
generic HTTP 400s are not. Other sessions must either complete
or fail in that same expected way. The report labels expected failures
explicitly and exits zero only for that proof.

A same-backend continuation can work without affinity. If the bounded run
does not cross backends, it is **inconclusive**, not a reason to claim success
or retry indefinitely. A missing-cookie policy rejection, generic 404/503,
deadline, missing member, or even a successful backend switch is not accepted
as the desired missing-response proof. Do not loosen assertions to force a
passing demonstration.

## 4. Affinity ON: prove pinned success

Keep **all other deployment inputs unchanged**, including publisher metadata.
The additional opt-in permits only this boolean to change on the owned lab:

```powershell
$env:LAB_SESSION_AFFINITY_ENABLED = 'true'
& .\scripts\Invoke-Lab.ps1 -Action Provision -Driver $driver -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowDeploy -AllowAffinityChange

$runtime = Get-Content ".\artifacts\lab\$environment\runtime.json" -Raw | ConvertFrom-Json
if ($runtime.LAB_SESSION_AFFINITY_ENABLED -ne 'true') { throw 'Affinity update is not verified.' }
& .\scripts\Invoke-Lab.ps1 -Action Run -Driver $driver -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowCredentialRead -ClientPath $client -ClientArguments @('--allow-requests')

# Unchanged rerun: same inputs; no affinity-change opt-in is needed.
& .\scripts\Invoke-Lab.ps1 -Action Provision -Driver $driver -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowDeploy
```

Default healthy execution plans **24 requests**: four independent three-turn
stateful sessions, then 12 stateless calls. Each stateful first turn receives
an opaque native cookie; subsequent turns retain only that session's cookie
and latest response ID. All turns must succeed, keep their original backend,
and collectively observe both members. Stateless calls never carry cookies
or previous IDs; distribution is checked separately. No exact 50/50 split
is promised.

Requests remain serial, with ten-second spacing, a 60-second per-request
deadline and a ten-minute scenario deadline. The two default demo runs plan
at most **36 attempts**, each requesting at most 2,048 output tokens.
Budget any extra runs explicitly. The
[harness reference](../src/ResponsesLab/README.md#configuration-and-limits)
lists all bounds and failure accounting.

For the reverse comparison, set the variable to `false` and use the same
`Provision -AllowDeploy -AllowAffinityChange` command. Use new sessions
after changing modes; never carry a cookie or response ID across the switch.
Changed models/names/targets/capacity still require a new environment.
Old ownership files lacking the new fixed-input fingerprint cannot use this
exception; do not migrate them by hand.

## 5. Inspect evidence in the owned workspace

Each client JSON line includes synthetic prompt/assistant text, session/turn,
previous/current response ID, actual member, request UUID, HTTP status and
outcome. Cookie values and keys are withheld; a cookie fingerprint is not a
routing credential. The final table includes failed and unattempted work.
Keep raw output local until reviewed and redacted.
The [actual redacted console sample](../evidence/samples/task008-console.md)
shows the emitted turn fields and final session accounting, with consistent
identifier aliases rather than real routing credentials.

Read workspace identity from the runtime file, not an assumed name:

```powershell
$runtime = Get-Content ".\artifacts\lab\$environment\runtime.json" -Raw | ConvertFrom-Json
$runtime.LAB_WORKSPACE_RESOURCE_ID
$runtime.LAB_WORKSPACE_CUSTOMER_ID
```

In Azure Portal, open **that workspace > Logs** and substitute the actual
`gatewayRequestId` from a client line:

```kusto
ApiManagementGatewayLogs
| where TimeGenerated > ago(1h)
| where CorrelationId == "<gatewayRequestId>"
| project TimeGenerated, CorrelationId, BackendId, BackendUrl,
          ResponseCode, BackendResponseCode, LastErrorReason, TraceRecords
```

For a whole run, substitute its exact request IDs:

```kusto
let requests = dynamic(["<request-id-1>", "<request-id-2>"]);
ApiManagementGatewayLogs
| where TimeGenerated > ago(1h)
| where CorrelationId in (requests)
| summarize Rows=count(), Traced=countif(isnotempty(TraceRecords))
          by CorrelationId, BackendId, BackendUrl, ResponseCode
```

The corresponding CLI command can run the same query:

```powershell
$query = 'ApiManagementGatewayLogs | where TimeGenerated > ago(1h) | summarize Rows=count(), Traced=countif(isnotempty(TraceRecords)) by ResponseCode'
az monitor log-analytics query --workspace $runtime.LAB_WORKSPACE_CUSTOMER_ID --analytics-query $query --timespan PT1H --output table
```

Allow up to **ten minutes** for per-request ingestion, then record missing
rows/traces as missing proof. Inspect actual outbound policy trace/member,
concrete backend host and status, not a model-authored claim. APIM body
capture is zero; credentials, cookie headers and client IP are not captured
by the API diagnostic. Native policy traces are not full debugger transcripts.
Other resource logs/query audits can contain metadata; use synthetic inputs.
Inherited central diagnostics have separate retention and are not removed by
deleting this workspace. See the [diagnostic contract](../infra/README.md#diagnostics-inventory-and-privacy).

## 6. Optional controlled outage

This is separate from the affinity comparison and needs explicit
fault/restoration authorization. The [failure scenario](../src/ResponsesLab/README.md#controlled-failure-and-recovery)
requires affinity on, temporarily changes one owned backend URL path, proves
pinned rejection versus fresh healthy sessions, then conditionally restores
and recovers the original chain. It does not delete a model, force traffic
to another account, or silently restart history.

## 7. Delete the exact owned lab

Save reviewed evidence first: deletion removes the workspace too.
Use the same driver, target variables and local ownership record:

```powershell
& .\scripts\Invoke-Lab.ps1 -Action Down -Driver $driver -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowDelete -ConfirmResourceGroup $group

$exists = az group exists --subscription $subscription --name $group --output tsv
if ($LASTEXITCODE -ne 0 -or $exists -ne 'false') { throw 'Group absence is not verified.' }
$state = Get-Content ".\artifacts\lab\$environment\state.json" -Raw | ConvertFrom-Json
if (-not $state.retired) { throw 'Local state is not retired.' }
if (Test-Path ".\artifacts\lab\$environment\runtime.json") { throw 'Active runtime remains.' }
```

Do not delete or edit ownership state to bypass a failed check. If azd state
is incomplete, use the same guarded `Down -Driver Cli` path. For transient
APIM `ServiceLocked`, inspect/wait for the transition and retry only that
owned group's guarded teardown. A polling/auth error requires exact state
reconciliation, not an assumption that deletion succeeded.

Local ownership/azd records remain; APIM, Foundry and workspace soft-delete
records/name reservations can remain, as can retained model quota.
**No purge is performed.** Reusing a retired environment is refused.
Cleanup, token/capacity controls and evidence boundaries are part of the lab.
