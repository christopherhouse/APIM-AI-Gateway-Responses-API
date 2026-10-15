# Deployment lifecycle - task 004

**Implemented and offline-tested; not deployed or live-verified.** These scripts
operate on a new, dedicated lab group only. Task `005` now provides the
[.NET harness](../src/ResponsesLab/README.md); task `006` adds guarded
fault/recovery automation. The clean-environment Azure walkthrough remains
tasks `007`-`008`.
Do not run the live examples without explicit target/action authorization.

## Prerequisites and target

Run from the repository root in **PowerShell 7** (`pwsh`), not Windows
PowerShell 5.1. Install Azure CLI **2.83.0+** with Bicep; local checks used
Bicep `0.47.16`. The `Azd` driver additionally requires Azure Developer CLI
**1.36.0+**. Native `.bicepparam` inputs are shared by both drivers.
Resource-group-scoped `azd` deployment is documented as beta.

Sign in beforehand with `az login`; for the `Azd` driver, also use
`azd auth login` as the same operator/tenant. The scripts do not log in, change
the active subscription, register providers, or choose another region.
The `Azd` driver explicitly requests JSON token output and compares identity
claims from short-lived management tokens held in memory; it does not print
or persist those tokens. For automation, azd's supported Azure CLI delegated
authentication is also usable: configure `auth.useAzCliAuth` in an isolated
`AZD_CONFIG_DIR` under ignored `artifacts/`, leaving global azd settings
unchanged. The same operator/tenant check still applies.

The operator needs subscription-scope group-create permission and sufficient
resource deployment/read permissions at the new group, including role
assignment writes for the two Foundry-scoped gateway grants. A narrowly scoped
resource contributor plus role-assignment administrator can be appropriate;
the scripts do not grant or elevate operator access. Deletion additionally
needs group-delete permission; `Run` needs APIM subscription read/listSecrets
access. Provider validation checks resource/RBAC permissions; effective
permissions do not override Azure Policy, deny assignments, or locks.

Required registered providers: `Microsoft.ApiManagement`,
`Microsoft.CognitiveServices`, `Microsoft.OperationalInsights`, and
`Microsoft.Insights`, and `Microsoft.ManagedIdentity`. Preflight refuses missing registration or regional
support rather than registering or falling back.

Replace these placeholders locally before any live command:

```powershell
# PowerShell 7, repository root. Do not commit real local values.
$subscription = 'REPLACE-WITH-ACTIVE-SUBSCRIPTION-UUID'
$environment = 'affinitydemo'
$group = "rg-responses-$environment"
$env:LAB_PUBLISHER_NAME = 'REPLACE-WITH-YOUR-PUBLISHER-NAME'
$env:LAB_PUBLISHER_EMAIL = 'REPLACE-WITH-YOUR-EMAIL'
$env:AZURE_LOCATION = 'westus3'
```

The subscription UUID is mandatory and must match `az account show`.
Environment names are 2-16 lowercase letters/digits/interior hyphens.
The dedicated group defaults to `rg-responses-<environment>`; an explicit
`-ResourceGroupName` must be 2-64 letters/digits/hyphens/underscores, beginning
alphanumeric. Displayed targets are checked against the saved local record.
Never reuse another application's group, even if empty.

| Configuration | Default / behavior |
| --- | --- |
| Region and APIM | Fixed `westus3`; Developer, one unit |
| `LAB_MODEL_NAME` / `LAB_MODEL_VERSION` | `gpt-5.4-mini` / `2026-03-17` |
| `LAB_MODEL_DEPLOYMENT` | `responses-chat` |
| `LAB_MODEL_SKU` / `LAB_MODEL_CAPACITY` | `GlobalStandard` / `20` per account |
| Publisher metadata | Required local values, no committed personal metadata |
| Resource names and diagnostics | Deterministic Bicep defaults; 30-day workspace retention, 1 GB/day ingestion quota |

Model overrides must still be GPT-5-family or newer, exactly available with
Responses support and an acceptable catalog lifecycle/capacity configuration.
The wrapper requires a declared maximum and enforces supplied minimum, step,
and allowed values. Some model catalog entries omit minimum/step; it prints
that limitation without inventing defaults. Provider validation and what-if
remain mandatory before either driver deploys.
The SKU cannot change; capacity is bounded to 1-20 per account. Only the
default model/version/capacity has task `001` live feasibility evidence.
This wrapper intentionally does not expose all lower-level Bicep overrides.

## Check, then provision

**Read-only preflight** requires its own opt-in:

```powershell
pwsh -NoProfile -File .\scripts\Invoke-Lab.ps1 -Action Preflight -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure
```

This checks sign-in/tools, the exact active subscription, owned-group inventory
when present, registered provider/region support, model/version/Responses
catalog metadata, quota, regional capacity, and effective permissions.
Quota checks subtract matching existing deployments on an unchanged rerun.
Missing/ambiguous service results fail, not default to success. Availability
is a snapshot, not a reservation.

If the group does not exist, preflight does **not** create it or run
group-scoped Provider validation. With an existing owned group, it also runs
Provider-level ARM validation and resource-level what-if. Even read-only
preflight writes ignored local ownership/compiled-parameter files.

Choose **one** driver for an environment:

```powershell
# azd: initialize bound local settings, preflight/create group, provision.
pwsh -NoProfile -File .\scripts\Invoke-Lab.ps1 -Action Provision -Driver Azd -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowDeploy

# Equivalent Azure CLI-only path: no azd installation or sign-in required.
pwsh -NoProfile -File .\scripts\Invoke-Lab.ps1 -Action Provision -Driver Cli -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowDeploy
```

Both use [`infra/deploy.bicepparam`](../infra/deploy.bicepparam), whose `using`
target is the same [`main.bicep`](../infra/main.bicep).
The wrapper creates/tags the group **outside Bicep**, validates ownership,
runs `az deployment group validate --validation-level Provider`, shows
`az deployment group what-if --validation-level Provider`, then invokes either
`azd provision` or `az deployment group create`. Do not approve provisioning
until you accept the intended resources and costs; the explicit deployment
opt-in covers the subsequent deployment, not an additional interactive prompt.
A validation failure can leave an empty owned group; cleanup is still explicit.

[`azure.yaml`](../azure.yaml) selects that resource group and native parameter
entry point. The wrapper creates the group before `azd` initializes its Bicep
provider, avoiding an uncontrolled group-creation prompt before the hook.
Four hooks recheck before provisioning/deletion and export/retire afterward.
Bare `azd provision`, `azd up`, and `azd down` are not supported entry points:
hooks require wrapper-provided action/target opt-ins. Do not add arbitrary
services or deployment layers to this dedicated-group configuration.

Developer APIM provisioning can be slow; keep the command attached and do not
start another operation against the same environment. This is billable
infrastructure, not a guaranteed low-cost SKU. APIM runtime, two model
allocations/inference, and diagnostics are cost drivers; quotas and ingestion
limits are not a monetary cap. No provisioning-duration or cost ceiling is
claimed for these undeployed scripts.

## Configuration and credentials

| Local path | Owner / contents |
| --- | --- |
| `artifacts\lab\<environment>\state.json` | Wrapper target, random ownership UUID, parameter hash, last successful output inventory, retirement status; no keys |
| `artifacts\lab\<environment>\compiled-parameters.json` | Generated native-parameter compiler output, never a maintained JSON parameter source |
| `artifacts\lab\<environment>\runtime.json` | Twelve allow-listed, non-secret client/ownership values |
| `artifacts\lab\<environment>\operation.lock` | Exclusive per-environment wrapper lock |
| `.azure\<environment>\` | azd-managed settings/state, including local publisher metadata and ordinary deployment outputs; never a credential store |

Keep these ignored files local and protect them. Do not delete/edit ownership
state to bypass a mismatch or adopt a group; loss of that record blocks reuse
and teardown through the wrapper and requires explicit operator investigation.
For an unchanged rerun, use the same environment, group, inputs, and sign-ins.
Changed targets or compiled inputs fail instead of silently replacing resources;
use a new environment/group for changed deployment inputs. An `Azd` rerun
synchronizes every supported input, including defaults, from the same compiled
parameters rather than trusting stale `.env` model values.
Source/template updates still require deliberate review; the input hash is not
a template-version lock. Do not run concurrent tools outside the wrapper.

After successful provisioning, both drivers export these names from ARM outputs:
`LAB_RESPONSES_ENDPOINT`, `LAB_MODEL_DEPLOYMENT`, `LAB_AFFINITY_COOKIE_NAME`,
`LAB_APIM_RESOURCE_ID`, `LAB_APIM_IDENTITY_RESOURCE_ID`, `LAB_APIM_SUBSCRIPTION_RESOURCE_ID`, `LAB_BACKEND_A_ID`,
`LAB_BACKEND_B_ID`, `LAB_FOUNDRY_A_RESOURCE_ID`, `LAB_FOUNDRY_B_RESOURCE_ID`,
`LAB_WORKSPACE_RESOURCE_ID`, and `LAB_WORKSPACE_CUSTOMER_ID`.
No endpoint guessing or key retrieval is needed for export. A newer failed or
incomplete deployment blocks stale successful output export.

```powershell
pwsh -NoProfile -File .\scripts\Invoke-Lab.ps1 -Action Export -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure
```

`Run` exports fresh configuration, checks that `lab-access` is active and scoped
to the Responses API, retrieves its primary key through management-plane
`listSecrets`, and starts an explicitly supplied executable. All client values
and `LAB_APIM_SUBSCRIPTION_KEY` are inherited through that process environment;
previous values/absence are restored in `finally`, including on client failure.
The key is never placed in arguments, output files, Bicep outputs, or
`azd env set`. Do not enable CLI debug/HTTP tracing, transcripts, or use an
untrusted client that could disclose inherited credentials.

Task `005` implements the .NET 10 client. Publish locally, then use its
additional request opt-in **only after real inference is separately approved**:

```powershell
dotnet publish .\src\ResponsesLab\ResponsesLab.csproj --configuration Release --output .\artifacts\publish\ResponsesLab
$client = (Resolve-Path .\artifacts\publish\ResponsesLab\ResponsesLab.exe).Path
# PowerShell 7, approved existing lab, same target variables as above.
& .\scripts\Invoke-Lab.ps1 -Action Run -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowCredentialRead -ClientPath $client -ClientArguments @('--allow-requests')
```

Invoke the script directly in PowerShell 7 for `-ClientArguments` arrays,
rather than a nested `pwsh -File` call. The client accepts
`--scenario both|stateful|stateless`, defaults to both, and exits nonzero for
request/evidence/affinity/distribution failures. The
[harness guide](../src/ResponsesLab/README.md) defines bounds and evidence.
Publishing/help and mocked tests passed locally; this end-to-end management
key handoff and inference have **not** been exercised against Azure.

### Separately authorized fault run

Task `006` adds the `failure` scenario with **both** wrapper and client fault
opt-ins and the exact group confirmation:

```powershell
& .\scripts\Invoke-Lab.ps1 -Action Run -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowCredentialRead -AllowFaults -ConfirmResourceGroup $group -ClientPath $client -ClientArguments @('--allow-requests', '--scenario', 'failure', '--allow-faults', '--confirm-resource-group', $group)
```

Do not execute this under code-only authorization. It conditionally changes only
one owned APIM backend's URL path on its same Foundry host, observes the pinned
failure/healthy fresh sessions, restores, and checks original-chain recovery.
The wrapper holds its operation lock and checks backend read/write permissions;
the client independently verifies ownership, endpoints, pool/breakers, and ETags.
ARM authentication uses the current Azure CLI identity in process memory.
No subscription or resource target is silently changed.

The [fault guide](../src/ResponsesLab/README.md#controlled-failure-and-recovery)
defines conservative timing, request/token bounds, expected-error rules,
independent cleanup timeout, and hard-stop limitations. An ignored
`artifacts\lab\<environment>\fault-recovery.json` is written before mutation
and removed only after verified restoration. An unresolved journal blocks a new
failure run; follow the guide's manual reconciliation rather than deleting it
or overwriting concurrent configuration. This workflow is offline-tested only.

## Scoped teardown

Archive only sanitized evidence first: deletion includes the workspace and its
queryable evidence. Both paths verify the active subscription, saved target,
exact group/region, ownership tags, allowed parent resource types/counts, and
the prior successful inventory when available. Unexpected resources, changed
ownership, missing local state, or a nonexact confirmation block deletion.
Tags/locks are guardrails, not protection against concurrent external writers.

```powershell
# Corresponding azd path; exact confirmation authorizes deletion of this group.
pwsh -NoProfile -File .\scripts\Invoke-Lab.ps1 -Action Down -Driver Azd -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowDelete -ConfirmResourceGroup $group

# CLI-only path; also usable for a partially provisioned owned group.
pwsh -NoProfile -File .\scripts\Invoke-Lab.ps1 -Action Down -Driver Cli -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowDelete -ConfirmResourceGroup $group
```

`azd down` can delete an **externally created** group; Bicep ownership does not
protect it. The wrapper uses `--force --purge=false --no-prompt` only after its
explicit confirmation/inventory checks, suppressing further deletion/purge
prompts without opting into permanent purge. CLI deletion uses `az group delete
--yes` for that same verified group. If azd initialization/deployment state is
missing or incomplete, use the guarded CLI path; do not broaden the target.

Both verify that the group is actually absent before retiring local state and
removing `runtime.json`. The ownership record and azd local files remain.
APIM, Foundry, and workspace recovery records/name reservations can remain;
Foundry quota may remain allocated until retention ends or a separately
authorized purge. No purge automation is included or implied by `-AllowDelete`.
Reprovisioning a retired environment is refused; use a new environment/group.

## Offline checks and live limits

```powershell
pwsh -NoProfile -File .\scripts\lifecycle.test.ps1
```

The test compiles native parameters locally, then replaces the external command
boundary with synthetic responses. It checks both drivers' ordering and
teardown, target/owner/identity mismatches, quota/catalog/provider/permission
failures, unchanged reruns, failed-deployment output refusal, allow-listed
configuration, opt-ins, and key restoration. It does not sign in, read a live
subscription, or retrieve a real key. See the
[infrastructure checks](../infra/README.md#local-validation-no-azure-sign-in-required)
for Bicep and gateway contracts.
The test needs both CLIs installed for executable discovery, although only the
local `az bicep build-params` command is actually executed.

Task `007` must exercise both clean-environment drivers, real catalog/API/RBAC
shapes, Provider validation/what-if, provisioning/reruns, identity propagation,
key handoff to the task `005` client, diagnostics/privacy, and teardown plus
recovery records. Offline mocks, source inspection, and the old disposable
experiment do not satisfy those live acceptance criteria.

| Failure | Safe response |
| --- | --- |
| Active subscription or azd identity mismatch | Correct your sign-ins/active context explicitly, then rerun against the same target |
| Provider, quota, region, model, policy, or permission refusal | Resolve the named prerequisite through an authorized operator; do not substitute targets or suppress checks |
| Changed inputs, owner, or inventory | Investigate local/Azure drift; do not edit the ownership record to force adoption or deletion |
| Native command failure with output withheld | The command failed and stopped the operation; check sign-in/permissions and separately authorized validation without credential/debug logging |
| Partial provisioning or azd state failure | Retain local ownership files; use the exact guarded CLI teardown when approved |
| Operation lock in use | Wait for the other wrapper invocation; do not bypass the lock |

## Official lifecycle references (inspected 2026-10-15)

- [azd resource-group-scoped deployments](https://learn.microsoft.com/azure/developer/azure-developer-cli/resource-group-scoped-deployments)
- [azd schema and hooks](https://learn.microsoft.com/azure/developer/azure-developer-cli/azd-schema)
- [azd environment variables](https://learn.microsoft.com/azure/developer/azure-developer-cli/manage-environment-variables)
- [azd 1.36.0 Bicep provider: native parameters, initialization, and destruction](https://github.com/Azure/azure-dev/blob/azure-dev-cli_1.36.0/cli/azd/pkg/infra/provisioning/bicep/bicep_provider.go)
- [Bicep CLI deployment and Provider validation](https://learn.microsoft.com/azure/azure-resource-manager/bicep/deploy-cli)
