# Platform evidence and design contract

**Verified:** 2026-10-15. **Task:** `001`.
**Status:** Task `001` complete; disposable feasibility evidence resolves
**B1 and B2**, and cleanup is verified. This document specifies contracts for
later tasks; it is not the complete self-service lab or authorization to start
tasks `002`-`008`.

## 1. Gate results and remaining decisions

Source IDs below refer to the official references in section 10. "Documented"
means documentation/schema evidence, not a successful live gateway test.

| Plan gate | Finding | State |
| --- | --- | --- |
| 1. Developer features | Developer, one unit, successfully ran the pool, cookie affinity, breakers, policies, and native Monitor trace path. No SLA. S1-S3 | Live feasibility passed |
| 2. Foundry/model/region | `AIServices`, `S0`, `gpt-5.4-mini` version `2026-03-17`, `GlobalStandard`, Responses v1. Both accounts/deployments created in West US 3 and served inference; processing can be global. S4-S7, S23 | Live feasibility passed |
| 3. Affinity and evidence | Four independent three-turn chains stayed on their member. Outbound effective host identified A/B; `context.Backend.Id` reported the pool. Debug URLs and workspace `BackendId`/`BackendUrl` corroborated the host mapping. S1, S8 | B2 resolved |
| 4. Pinned failure | Existing A cookie selected A with its breaker open, returning 503 without forwarding. Three fresh sessions reached B; the original chain recovered on A after restoration. This is a bounded observation, not a universal SLA. S1-S3 | B1 resolved |
| 5. Traces | All 54 captured response IDs joined to gateway rows with nonempty native policy `TraceRecords`. Full debugger transcripts remained separate and in memory. S8-S11 | Probe ingestion verified; full-lab task `007` still pending |
| 6. Resource diagnostics | Parent category discovery matched section 6; child/control-plane inventory is explicit. Probe enabled only `GatewayLogs`; all-category implementation remains `002`. S12-S15 | Design inventory established |
| 7. Authentication | API-scoped APIM subscription access; gateway system identity had OpenAI User at both account scopes. Inference succeeded with Foundry local auth disabled. S16 | Live feasibility passed |
| 8. Group lifecycle | Resource-group-scoped `azd` is documented as beta. Bicep does not create the group. `azd down` can delete an externally created group. S17-S18 | Ownership contract selected; beta noted, clean-environment behavior still needs task `007` |

### Resolved design gates

| ID | Original gap | Evidence and decision |
| --- | --- | --- |
| B1 | Cookie plus open-breaker interaction was unverified. | Native behavior met the requested explicit pinned failure in this test: 503, `CircuitBreakerIsOpen`, selected A, no backend execution. Keep native affinity; no pin cache/token workaround. |
| B2 | Same-region labels and a pool context ID cannot prove A/B. | Map outbound `context.Request.Url.Host` against generated allow-listed account hosts. Cross-check actual debug forwarding URLs and workspace member fields. Do not use `context.Backend.Id` as the concrete-member response header. |

Task `002` still requires user authorization and an appropriate dependency
branch/merge decision. No custom pin token, response-ID lookup cache, or extra
trace collector is needed. The separate task `001` deployment/fault/cleanup
approval did not authorize later tasks.

For the agreed APIM trace-record requirement, select policy-generated traces
plus correlated gateway logs. This is actual trace content, not a substitution
of correlation IDs or access logs alone. Automatic ingestion of full debugger
transcripts is not promised; adding it would require separately approved scope.

## 2. Read-only Azure evidence

Checks used the already-active Azure CLI subscription in `AzureCloud`, state
`Enabled`; no subscription or region was changed. Identifying subscription
settings and raw environment output are deliberately not versioned. Deployment
must re-read, display, and confirm the actual target before any write.

Azure CLI version: `2.83.0`. All observations are snapshots, not reservations or
proof that a later deployment will succeed.

| Check | Observed result |
| --- | --- |
| `az account show` | Active AzureCloud subscription enabled |
| `az provider show --namespace Microsoft.ApiManagement` | Registered; `service` lists West US 3, `2024-05-01`, and `2025-03-01-preview` |
| `az provider show --namespace Microsoft.CognitiveServices` | Registered; accounts support `2025-06-01` |
| `az provider show --namespace Microsoft.OperationalInsights` | Registered; workspaces list West US 3 and `2023-09-01` |
| `az cognitiveservices model list --location westus3` | `AIServices` catalog includes `gpt-5.4-mini`, `2026-03-17`, Responses capability `true`, `GlobalStandard` SKU |
| `az cognitiveservices usage list --location westus3` | Exact quota key `OpenAI.GlobalStandard.gpt-5.4-mini`: current `50`, limit `1000`, available `950`, unit `Count` |
| Model Capacities List, API `2025-06-01`, filtered to West US 3 + GlobalStandard | Available capacity `950` for the selected model/version |

The capacity endpoint is the subscription-level
`Microsoft.CognitiveServices/modelCapacities` endpoint, with `modelFormat`,
`modelName`, and `modelVersion` query parameters; filter the returned location
and SKU. S7 documents its shape. On Windows, protect URLs containing `&` from
`az.cmd` argument splitting; the successful check used PowerShell `az --%`.

Use each SKU's returned `usageName` for quota matching, not a guessed model
name. For example, the previously examined `gpt-4.1-mini` maps to
**`gpt4.1-mini`** in its quota key, unlike the selected GPT-5.4 mini quota key.
An empty filtered result is not zero quota. Initial CLI quoting/filter attempts
were corrected before recording these successful results.

### Model choice and limits

Following the user's explicit request to use at least a GPT-5-family model,
select two identical deployments named `responses-chat`:

| Setting | Selected value |
| --- | --- |
| Resource | `Microsoft.CognitiveServices/accounts`, kind `AIServices`, SKU `S0` |
| Location | `westus3` for each account; `GlobalStandard` inference may execute in other Azure regions |
| Model | Format `OpenAI`, name `gpt-5.4-mini`, version `2026-03-17` |
| Deployment | `GlobalStandard`, capacity `20` per account; total quota allocation `40` |
| Version upgrades | Explicit version with `NoAutoUpgrade`; revalidate lifecycle before deployment |
| Current catalog lifecycle | `GenerallyAvailable`; SKU retirement timestamp corresponds to **2027-09-21 UTC** |
| Rate metadata | Per capacity unit: `1000` tokens and `1` request per `60` seconds |
| Initial harness budget | Four stateful sessions, three turns each; twelve independent stateless calls; concurrency one; at least ten seconds between request starts |
| Request/run bounds | `max_output_tokens=2048` including reasoning, `reasoning.effort=low`, request timeout 60 seconds, run deadline ten minutes, automatic request retries zero |

Capacity `20` therefore budgets approximately 20,000 TPM and 20 RPM per
deployment using the returned metadata; actual service throttling still governs.
Recheck quota, available capacity, lifecycle, and permissions immediately before
deployment. `NoAutoUpgrade` is not a retirement exemption.

The mini model passed short marker-based feasibility conversations; the complete
architecture-expert script and quality evaluation remain task `005`/`007`.
No price/cost ceiling is claimed. Its reasoning and visible
output share the output-token budget. An `incomplete` response or missing answer
must fail the scenario rather than count as a successful turn. S24.

**Resource location is not inference residency:** `GlobalStandard` is a
deliberately selected deployment type, not a claim that processing stays in
West US 3. Both Foundry account resources remain in West US 3; global inference
can execute elsewhere. S23. If geography-constrained inference is an additional
requirement, revisit deployment type/model support before implementation.

The initially examined `gpt-4.1-mini` regional candidate was superseded by the
GPT-5 requirement. The older `gpt-4o-mini` regional Standard entry was also
rejected because its returned SKU retirement date was already in the past.

## 3. Resource and API choices

These versions are intentional design pins, not claims to be the latest.
Implementation tasks must build/lint against them and revisit documented changes.

| Resource/contract | Version or setting | Reason/source |
| --- | --- | --- |
| APIM service, APIs, operations, policies, subscriptions, loggers, API/service diagnostics | `2024-05-01` | Stable APIM control-plane baseline; S9, S19, S26 |
| APIM backend entities and pool | `2025-03-01-preview` | S3 includes `pool.sessionAffinity`, source **`cookie`**. The stable `2024-05-01` backend schema lacks that property; do not copy the uppercase source in older illustrative snippets. |
| Foundry accounts and model deployments | `2025-06-01` | Account/deployment schemas; S5-S6 |
| Log Analytics workspace | `2023-09-01`, `PerGB2018`, retention intent 30 days | S20 |
| Azure Monitor diagnostic settings | `2021-05-01-preview` | Workspace destinations, log categories and resource-specific mode; S15 |
| Role assignments | `2022-04-01` | Resource-scoped role assignments; S21 |
| Model data plane | `POST /openai/v1/responses`, no dated `api-version` query | Responses v1; S4 |

Use one new Developer gateway, two new Foundry accounts with public network
access and custom subdomains, one workspace, and scoped role/diagnostic child
resources. No Foundry agent/project, Cosmos DB, Redis, Key Vault, Application
Insights, or trace ingestion service is currently required by the proposed
policy-trace design. Public access is not anonymous access. Deployment preflight
must check organizational policy; do not bypass a policy or change network
requirements silently.

## 4. Routing, state, and per-turn evidence

### State boundary and transport

- **Foundry:** Stores the response state referenced by `previous_response_id`.
  Response data is retained for 30 days by default; retention is not a promise
  that a cookie or lab remains valid for that duration. S4.
- **Gateway:** Chooses a backend through a native pool and transports its opaque
  affinity cookie. No model-history replay or response-ID mapping store.
- **Harness:** Owns session ID, latest successful response ID, expected backend
  label, and an isolated cookie container. Never share a cookie handler across
  sessions or with the stateless scenario.

Configure the pool with equal weights and priorities, cookie source `cookie`,
and cookie name `APIM-Backend-Affinity` (the name used by S22). Do not parse,
forge, reconstruct, or log cookie values. Use HTTPS and the normal cookie
domain/path/expiry rules. Discard session routing state when that session ends.

### Request paths

| Path | Required behavior |
| --- | --- |
| First stateful turn | New harness session UUID; no cookie or previous response ID. Send `store=true`, `stream=false`, `model=responses-chat`; select through the pool. Retain the issued affinity cookie and response ID only after a successful response with valid evidence. |
| Chained turn | Send that session's cookie and latest `previous_response_id`. Missing/expired affinity information is an explicit client/gateway validation failure, not a new conversation. Keep the same expected backend. |
| Stateless call | Independent request with no previous ID, `store=false`, and no cookie replay. Ignore `Set-Cookie`; never borrow a stateful session's handler. |
| Pinned backend failure | Preserve the chain and original routing context; no retry onto another backend or history replay. Observed initial real backend 404, then native 503 / `CircuitBreakerIsOpen` with no forwarding while that member's breaker was open. Do not require JSON error bodies. |
| Successful HTTP with missing or changed backend evidence | Fail the harness assertion, preserve diagnostics, and stop that chain. A 2xx alone is not an affinity pass. |

### Gateway-derived identity and correlation

The **required** member labels are `backend-a` and `backend-b`. Their mapping
must be generated from the two deployed account hostnames, not model output.
The verified signal is the effective `context.Request.Url.Host` in the outbound
section after forwarding, allow-listed against those hosts. Debug backend
request URLs and workspace member fields agreed. Unknown host, pool-only
identity, or missing identity on a successfully forwarded request is a failure,
never an inferred label. Same-region headers cannot prove
this lab's affinity.

The current policy-expression reference explicitly exposes `context.Backend.Id`
and `Type` (`Single` or `Pool`). S25. Their existence does not establish which
member is reported after pool selection; the experiment records both the
context ID and effective request host for comparison with actual backend URLs.

| Name | Owner and contract |
| --- | --- |
| `X-Lab-Session-Id` | Harness-generated UUID, validated at the gateway; correlation label only, not a routing credential |
| `X-Lab-Request-Id` | Gateway overwrites with `context.RequestId`; return on success and policy/error paths where possible |
| `X-Lab-Backend-Id` | Gateway overwrites after forwarding with the verified concrete-member label; absent for pre-forwarding rejection rather than inventing an executed backend |
| `traceparent` | Optional standards-based trace correlation, not evidence of which backend executed |
| `Apim-Trace-Id` | Optional debug-trace ID only when an authorized debug request actually produces it |

The harness prints prompt/response, scenario, session ID, turn, response ID,
previous ID, backend label, gateway request ID, status/error, and outcome. A
short cookie fingerprint may be used locally, never the cookie itself. For each
session, summarize backend set, attempts, successful turns, failures, incomplete
turns, and affinity violations. Report stateful and stateless distribution
separately; observing both members matters, not an exact 50:50 split.

Do not fabricate request IDs, missing trace IDs, or successful evidence. Missing
required fields and unexpected scenario outcomes produce a nonzero exit code.

## 5. Breaker and reversible-fault proposal

This **lab-only mechanism** passed the disposable task `001` experiment. It is
an endpoint-misconfiguration simulation, not a physical Azure outage.

- One breaker rule per backend: count `1` failure within `PT1M`, trip duration
  `PT30S`, `acceptRetryAfter=true`, status ranges `404`, `429`, and `500-599`.
  Count-based detection is approximate and local to gateway instances. A
  `Retry-After` can extend the nominal pause. S1, S3.
- Proposed injection: after establishing a successful pinned session, change
  only that lab backend entity's URL path to a reserved nonexistent path on
  its **same** Foundry host. This tests an unavailable/misconfigured endpoint,
  not a physical service outage. It is expected to elicit a real backend 404;
  that exact response and breaker accounting must be proven before adoption.
- Never use an inbound `return-response` as evidence of backend breaker
  detection, delete a model to simulate failure, or send credentials to an
  arbitrary fault host.
- Capture original backend configuration/version, verify dedicated group
  ownership and both allow-listed backend IDs, and refuse concurrent
  configuration changes. Patch only the approved field; use a scoped restore
  in `finally` and report restoration failures separately from scenario failures.
- First observe the pinned backend error, then require three consecutive fresh
  sessions on the healthy member and repeat the original cookie **inside the
  30-second open window**. The successful experiment used two seconds between
  those checks; normal traffic used ten seconds. Capture every attempt and
  actual timestamps. A ten-second pace across four fresh checks can outlast
  the breaker: an earlier probe observed another A/404 after expiry. This is
  expected rediscovery of a persistent fault, not an indefinite healthy-only
  routing guarantee. There are no automatic transport or chain retries.
- Restore the URL, wait out the actual breaker pause, and observe recovery.
  Failure to establish both backends, unexpected error codes, timeout, missing
  evidence, a changed pin, or failed restoration makes the scenario fail.

An initial backend 404, a documented breaker-open rejection, a pool fallback
attempt, and an unrelated authentication error are different outcomes. Do not
normalize all of them to "successful failover." The verified pinned-open
response was a non-JSON 503, with policy reason `CircuitBreakerIsOpen`, selected
`BackendId=backend-a`, empty `BackendUrl`, and no backend response/forwarding
step. Never invent an executed-member header for that rejection.

## 6. Diagnostics, trace destinations, and retention

Select the **native policy-trace path** for the agreed trace-record requirement:
enable Azure Monitor gateway logging with resource-specific destination mode,
100% sampling for this small lab, information verbosity, no client-IP capture,
zero request/response body bytes, and only allow-listed correlation headers.
Emit compact `trace` records in supported policy sections for route/outcome
events; inspect diagnostic error fields for gateway failures. S8-S10.

Trace records contain scenario/session/request identifiers, verified backend
label when available, phase, and outcome; do not include prompts, completions,
authorization headers, keys, cookies, or raw payloads. The console can show the
scripted prompts/responses; raw console output stays ignored and must be
sanitized before sharing.

| Resource | Documented log category inventory | Workspace destination / caveat |
| --- | --- | --- |
| APIM service | `GatewayLogs`, `GatewayLlmLogs`, `GatewayMCPLogs`, `DeveloperPortalAuditLogs`, `WebSocketConnectionLogs` | `ApiManagementGatewayLogs` for gateway evidence; other categories may have no events because that workload is unused. S12 |
| Each Foundry account | `Audit`, `AzureOpenAIRequestUsage`, `ManagedNetworkEvent`, `RequestResponse`, `Trace` | `AzureDiagnostics`; categories depend on resource capabilities. Inspect privacy/contents before enabling any payload-rich option. S13 |
| Log Analytics workspace | `Audit`, `Jobs`, `SummaryLogs` | `LAQueryLogs`, `LAJobLogs`, `LASummaryLogs`; workspace itself belongs in diagnostics inventory. S14 |
| APIM APIs/backends/policies/subscriptions, model deployments, role assignments, diagnostic settings, externally managed group | No separate log categories established for these child/control-plane resources | Record as not independently configured, not silently "all resources logged." Their resource changes are a separate Activity Log concern. |

Once resources exist, query their diagnostic category endpoints. Configure every
returned supported log category and exportable metric category toward the lab
workspace; fail on unaccounted-for differences instead of silently omitting
categories. If a child resource unexpectedly supports diagnostics, update the
inventory and configuration. Synthetic traffic must be non-sensitive. Account
`RequestResponse` logs are not permission to enable content capture indiscriminately.

During the disposable feasibility deployment, category discovery returned all
five listed APIM categories, all five listed Foundry categories, and all three
workspace categories above, plus `AllMetrics` for each parent type. The probe
enables only `GatewayLogs`, sufficient for B1/B2 and trace correlation; the
full all-supported-category deployment remains task `002`, not completed here.

Retain workspace data for 30 days for the lab; align supported table settings.
Do not configure a workspace data-export loop. Inventory category availability
separately from actual emission: enabling Jobs or WebSocket logs does not mean
the scenario should manufacture those events.

### Policy records versus debugger transcripts

`TraceRecords` is a documented dynamic field for records emitted by `trace`
policies. Query it alongside `CorrelationId`, `BackendId`, `BackendUrl`, response
codes, and errors. Access logs alone do not meet the policy-trace assertion. S8.

Full per-request debugging instead uses a short-lived API debug credential,
`Apim-Debug-Authorization`, the returned `Apim-Trace-Id`, and management-plane
trace retrieval. The obsolete `Ocp-Apim-Trace` header is not the design.
Full transcripts are potentially sensitive and are **not** claimed to be
automatically ingested into Log Analytics. S11.

Query shape verified against the disposable workspace:

```kusto
ApiManagementGatewayLogs
| where TimeGenerated > ago(30m)
| where CorrelationId == "<gateway-request-id>"
| project TimeGenerated, CorrelationId, BackendId, BackendUrl,
          ResponseCode, BackendResponseCode, TraceRecords
```

The probe verified `context.RequestId` equals `CorrelationId`; `TraceRecords`
contained the compact inbound/outbound/on-error messages. Task `007` must
repeat the checks for the complete lab. Allow up to ten minutes of bounded ingestion
polling; a missing expected record after the budget is a failed check, not proof
that diagnostics work.

## 7. Authentication and least privilege

| Actor/path | Selected access |
| --- | --- |
| Local deployment operator | Existing Azure CLI/azd sign-in to the confirmed target; resource write permissions at the dedicated group and permission to assign the two account-scoped roles. Group creation additionally needs subscription-scope group-create permission. Existing operator permissions were not broadened; the probe created only its gateway's two resource-scoped assignments. |
| APIM to each Foundry | Gateway system-assigned identity; `Cognitive Services OpenAI User` (`5e0bd9bd-7b93-4f28-af87-19fc36ad61bd`) separately scoped to each account. `authentication-managed-identity` targets `https://cognitiveservices.azure.com`; fail token acquisition explicitly. S16 |
| Harness to APIM | API-scoped APIM subscription, header `Ocp-Apim-Subscription-Key`. No all-APIs/master subscription, anonymous API, or key in a query string. Subscription access is independent from backend Entra authentication. |
| Evidence reader | Log Analytics read/query access at the lab workspace, not subscription-wide administrator access |
| Debug-trace operator | Separately authorized management-plane debug-credential/retrieval permissions; not granted to ordinary model callers |

Set Foundry `disableLocalAuth=true`; no model API keys are needed. Replace
caller `Authorization`/`api-key` with the gateway identity credential rather
than trusting caller backend authentication, and do not forward the APIM
subscription key to Foundry. Keep TLS hostname/chain validation enabled.

Retrieve the APIM subscription key only through an authorized management-plane
operation and put it directly in the harness process environment. Do not print
it, save it through `azd env set`, or expose it as a Bicep output. CI, if later
authorized, should use workload identity rather than committed credentials.

## 8. Shared configuration and deployment ownership

Names below are the proposed contract for tasks `002`-`005`, not files or
commands available today.

| Bicep input | Orchestration / harness name | Value or meaning |
| --- | --- | --- |
| `environmentName` | `AZURE_ENV_NAME` | Environment identifier; lab ownership tag |
| Deployment scope, not group resource | `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP` | Confirmed active subscription and new dedicated group |
| `location` | `AZURE_LOCATION` | `westus3`; reject silent fallback |
| `apimName`, `foundryAName`, `foundryBName`, `workspaceName` | Non-secret environment parameters | New lab resource names; validate global uniqueness when relevant |
| `publisherName`, `publisherEmail` | Local operator inputs | Required APIM metadata; never commit personal values |
| `modelName`, `modelVersion`, `modelDeploymentName` | `LAB_MODEL_NAME`, `LAB_MODEL_VERSION`, `LAB_MODEL_DEPLOYMENT` | `gpt-5.4-mini`, `2026-03-17`, `responses-chat` |
| `modelDeploymentSku`, `modelCapacity` | `LAB_MODEL_SKU`, `LAB_MODEL_CAPACITY` | `GlobalStandard`, `20` per backend |
| `logRetentionDays` | `LAB_LOG_RETENTION_DAYS` | `30` |
| `affinityCookieName` | `LAB_AFFINITY_COOKIE_NAME` | `APIM-Backend-Affinity`, not its secret value |

Non-secret outputs: `apimGatewayUrl`, `responsesEndpoint`, `apimResourceId`,
`apimSubscriptionResourceId`, `backendAId`, `backendBId`, `foundryAResourceId`,
`foundryBResourceId`, `foundryAEndpoint`, `foundryBEndpoint`,
`workspaceResourceId`, `workspaceCustomerId`, and `modelDeploymentName`.
Map endpoint/deployment outputs directly to `LAB_RESPONSES_ENDPOINT` and
`LAB_MODEL_DEPLOYMENT`; the harness must not guess URLs. Its only secret input
is `LAB_APIM_SUBSCRIPTION_KEY`, handled as described above.

Harness controls use `LAB_STATEFUL_SESSION_COUNT=4`, `LAB_TURNS_PER_SESSION=3`,
`LAB_STATELESS_REQUEST_COUNT=12`, `LAB_MAX_OUTPUT_TOKENS=2048`,
`LAB_REASONING_EFFORT=low`, `LAB_REQUEST_TIMEOUT_SECONDS=60`,
`LAB_RUN_TIMEOUT_SECONDS=600`, `LAB_REQUEST_INTERVAL_SECONDS=10`, and
`LAB_MAX_RETRIES=0`. Validate bounds rather than silently coercing invalid
values. Use `LAB_FAILURE_PROBE_INTERVAL_SECONDS=2` for the bounded three-fresh
plus original-cookie checks inside the 30-second open window, rather than
reusing the ten-second baseline interval. Fault execution defaults to
`LAB_ENABLE_FAULTS=false` and requires
separate authorization in addition to an explicit opt-in.

The gateway frontend contract is `/openai/v1/responses`; derive its fully
qualified URL from APIM output. Derive each backend OpenAI endpoint from the
account's returned endpoint properties/custom subdomain, not a manually guessed
suffix. Configuration export must omit secrets.

### Group lifecycle for both deployment paths

- Orchestration creates the new dedicated lab group outside Bicep, with a
  verifiable environment/ownership tag. Never adopt an unrelated shared group.
- Bicep entry point is resource-group-scoped and contains no
  `Microsoft.Resources/resourceGroups` resource. `azd` selects that group through
  `AZURE_RESOURCE_GROUP`; CLI deployment uses that same group/template/inputs.
  The documented `azd` resource-group feature is beta. S17.
- The inspected `azd` Bicep provider checks external-group ownership before
  destruction because deleting that group can remove **all** its resources.
  Do not advertise externally created groups as immune to `azd down`. S18.
- Both teardown paths must verify exact subscription, group ownership, resource
  inventory, and explicit deletion authorization. Explain that group deletion
  can remove the workspace/evidence too. Archive only sanitized required
  evidence before teardown; account for soft-deleted resources.
- Test both `azd` and Azure CLI on clean environments in task `007`. Source
  inspection is not proof of an installed `azd` release's runtime behavior.

## 9. Feasibility experiment required to close the gates

The user explicitly authorized this experiment on 2026-10-15, including
temporary billable deployment, reversible fault, evidence, and cleanup. It is
recorded under `experiments/001-affinity/`, not a substitute for tasks
`002`-`008`. The fixed Developer SKU (one unit) and two backends were used:

1. Establish two identifiable members; cross-check a proposed gateway header
   with the actual backend request URL and correlated workspace record.
2. Capture a successful stateful session and retain its original cookie/ID.
3. Apply the reversible, scoped endpoint fault. Record the actual HTTP error.
4. Trigger the breaker with real forwarded failures; repeat the original cookie
   before and after the trip. Capture which member, if any, receives the request.
5. Prove fresh sessions reach the healthy backend within the chosen observation
   budget, then restore and prove recovery. Report all errors and timing.
6. Verify nonempty `TraceRecords` and the correlation join. If full debugger
   transcripts are required instead, pause for the separate approved design.

Only then close B1/B2 and finalize the design before task `002`. Do not claim
completion of task `001` merely because this document and a research PR exist.

### Observed feasibility result

- Four independent sessions completed three chained turns each, with zero
  observed member changes. A separate eight-call stateless sample observed
  A=4/B=4; this is an observation, not a balancing guarantee.
- Final failure run: A/200 and B/200 setup; A/404 after the same-host path fault;
  three fresh B/200 responses; original-cookie A selection rejected with 503 /
  `CircuitBreakerIsOpen`; restored original cookie/ID chain returned A/200.
  Workspace/debug records establish selection versus actual forwarding.
- All 54 captured responses correlated with nonempty policy traces. The
  workspace recorded 55 HTTP requests (49 successful, four injected-path 404s,
  two breaker 503s). There were 59 budgeted attempts total, including four
  transport failures and one earlier 503 whose metadata was lost before the
  error-body parser was fixed. These failures are not omitted from the budget.
- Initial deployment exposed a missing `azuremonitor` logger; the template now
  creates it explicitly. Intermittent TLS resets affected both fetch and curl;
  the final probe used a single keep-alive native HTTPS agent without retries
  or weakened TLS. One probe outlasted the breaker window; the final timing
  and non-JSON error handling were corrected and regression-tested.
- Recorded successful-response usage was 1,316 input and 300 output tokens,
  approximately $0.002337 at the checked model rates. This is not a complete
  bill or an assumption of zero cost for unknown attempts. Actual Azure
  invoiced cost is not yet available. Cleanup verification is recorded in
  [PROGRESS.md](PROGRESS.md).
- Ownership-checked group deletion was verified at **2026-10-15 18:20:36 UTC**.
  Both Foundry soft-deleted records were purged after confirming their tags
  and exact group; quota returned from 90 to the original 50 used units.
  APIM retains its deleted-service record until October 17; the workspace
  retains its normal 14-day recovery state. These are disclosed recovery
  records, not active lab resources. S27-S28.

Cost guardrails for this exception: three-hour lifetime; 64 model requests
maximum across reruns, 2,048 output tokens/request, short prompts; a 1 GB/day
workspace quota (can overshoot); target under $5, not a hard billing cap.
Current retail checks yielded Developer $0.0658/hour, model input/output
$0.75/$4.50 per million tokens, and positive log ingestion $2.30/GB.
The illustrative allowance of approximately $3.19 is not an actual invoice.
Exact private target settings stay ignored. An attached ownership-checking
cleanup watcher and verified group deletion are required.

## 10. Official sources

All references were inspected on **2026-10-15**. Versioned source links identify
the inspected upstream commit, not a deployment or local dependency.

| ID | Official source |
| --- | --- |
| S1 | [APIM backends, pools, session awareness, and circuit breakers](https://learn.microsoft.com/azure/api-management/backends) |
| S2 | [APIM tier feature comparison](https://learn.microsoft.com/azure/api-management/api-management-features) |
| S3 | [Backend schema: 2025-03-01-preview](https://learn.microsoft.com/azure/templates/microsoft.apimanagement/2025-03-01-preview/service/backends) and [stable 2024-05-01](https://learn.microsoft.com/azure/templates/microsoft.apimanagement/2024-05-01/service/backends) |
| S4 | [Azure OpenAI Responses API](https://learn.microsoft.com/azure/foundry/openai/how-to/responses) |
| S5 | [Foundry account schema: 2025-06-01](https://learn.microsoft.com/azure/templates/microsoft.cognitiveservices/2025-06-01/accounts) |
| S6 | [Model deployment schema: 2025-06-01](https://learn.microsoft.com/azure/templates/microsoft.cognitiveservices/2025-06-01/accounts/deployments) |
| S7 | [Model Capacities List: 2025-06-01](https://learn.microsoft.com/rest/api/microsoftfoundry/accountmanagement/model-capacities/list?view=rest-microsoftfoundry-accountmanagement-2025-06-01) |
| S8 | [ApiManagementGatewayLogs table, including TraceRecords](https://learn.microsoft.com/azure/azure-monitor/reference/tables/apimanagementgatewaylogs) |
| S9 | [APIM diagnostic schema and trace verbosity](https://learn.microsoft.com/azure/templates/microsoft.apimanagement/2024-05-01/service/diagnostics) |
| S10 | [Trace policy](https://learn.microsoft.com/azure/api-management/trace-policy) |
| S11 | [API request debugger tracing](https://learn.microsoft.com/azure/api-management/api-management-howto-api-inspector) |
| S12 | [APIM supported log categories](https://learn.microsoft.com/azure/azure-monitor/reference/supported-logs/microsoft-apimanagement-service-logs) |
| S13 | [Cognitive Services account supported log categories](https://learn.microsoft.com/azure/azure-monitor/reference/supported-logs/microsoft-cognitiveservices-accounts-logs) |
| S14 | [Workspace supported log categories](https://learn.microsoft.com/azure/azure-monitor/reference/supported-logs/microsoft-operationalinsights-workspaces-logs) |
| S15 | [Azure Monitor diagnostic settings schema](https://learn.microsoft.com/azure/templates/microsoft.insights/2021-05-01-preview/diagnosticsettings) |
| S16 | [APIM authentication to AI APIs](https://learn.microsoft.com/azure/api-management/api-management-authenticate-authorize-ai-apis) |
| S17 | [azd resource-group-scoped deployments](https://learn.microsoft.com/azure/developer/azure-developer-cli/resource-group-scoped-deployments) |
| S18 | [azd Bicep provider source at 3f6d09c](https://github.com/Azure/azure-dev/blob/3f6d09c2ad31deb69b40e67f56bebfb2ce072cbc/cli/azd/pkg/infra/provisioning/bicep/bicep_provider.go) |
| S19 | [APIM stable service schema](https://learn.microsoft.com/azure/templates/microsoft.apimanagement/2024-05-01/service) |
| S20 | [Workspace schema: 2023-09-01](https://learn.microsoft.com/azure/templates/microsoft.operationalinsights/2023-09-01/workspaces) |
| S21 | [Role assignment schema: 2022-04-01](https://learn.microsoft.com/azure/templates/microsoft.authorization/2022-04-01/roleassignments) |
| S22 | [Official session-awareness sample at 1679e31](https://github.com/Azure-Samples/AI-Gateway/blob/1679e3137d2fe7592d2a668d77ff71550d35b69e/labs/session-awareness/README.md) |
| S23 | [Model deployment types and global inference placement](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/deployment-types) |
| S24 | [Reasoning models, token budgets, and incomplete responses](https://learn.microsoft.com/azure/foundry/openai/how-to/reasoning) |
| S25 | [APIM policy expressions and context.Backend members](https://learn.microsoft.com/azure/api-management/api-management-policy-expressions) |
| S26 | [Explicit azureMonitor logger schema](https://learn.microsoft.com/azure/templates/microsoft.apimanagement/2024-05-01/service/loggers) |
| S27 | [APIM soft-delete and 48-hour recovery period](https://learn.microsoft.com/azure/api-management/soft-delete) |
| S28 | [Workspace deletion and 14-day recovery period](https://learn.microsoft.com/azure/azure-monitor/logs/delete-workspace) |
