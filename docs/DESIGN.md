# Platform evidence and design contract

**Verified:** 2026-10-15. **Task:** `001`.
**Status:** Design investigation recorded; **blocked, not implementation-ready**.
This document proposes contracts for later tasks; it is not a deployed solution.

## 1. Gate results and remaining decisions

Source IDs below refer to the official references in section 10. "Documented"
means documentation/schema evidence, not a successful live gateway test.

| Plan gate | Finding | State |
| --- | --- | --- |
| 1. Developer features | Classic Developer supports policies and Azure Monitor logs. Backend pools, cookie affinity, and breakers are documented; Developer has one scale unit and no SLA. S1-S3 | Documented |
| 2. Foundry/model/region | `AIServices`, `S0`, `gpt-5.4-mini` version `2026-03-17`, `GlobalStandard`, Responses v1. West US 3 resource placement; inference can be global. Catalog, quota, and capacity checks succeeded. S4-S7, S23 | Read-only checks passed; creation/inference not tested |
| 3. Affinity and evidence | Native opaque cookie transport is documented. A pool ID or region is not proof of the concrete backend member. The outbound member-identity contract below needs verification. S1, S8 | Blocked: B2 |
| 4. Pinned failure | Reviewed docs describe normal stickiness and breaker exclusion separately, not their interaction for an existing cookie. They do not establish the required pinned-failure contract. S1-S3 | Blocked: B1 |
| 5. Traces | `trace` policy records can reach `ApiManagementGatewayLogs.TraceRecords`. Full per-request debug traces are a separate mechanism, not automatically a workspace trace transcript. S8-S11 | Policy-trace path documented; interpretation decision B3 |
| 6. Resource diagnostics | Parent resources have documented categories; child/control-plane resources need an explicit unsupported-category inventory. See section 6. S12-S15 | Inventory documented; actual category discovery deferred until resources exist |
| 7. Authentication | API-scoped APIM subscription access for the harness; gateway system identity with resource-scoped OpenAI User roles for both Foundries. No model keys. S16 | Design selected; live authorization not tested |
| 8. Group lifecycle | Resource-group-scoped `azd` is documented as beta. Bicep does not create the group. `azd down` can delete an externally created group. S17-S18 | Ownership contract selected; beta noted, clean-environment behavior still needs task `007` |

### Blocking decisions

| ID | Gap | Condition to resume task `001` |
| --- | --- | --- |
| B1 | No verified native contract for an existing affinity cookie after its backend breaker opens. This is an evidence gap, **not proof that native affinity is unsupported**. | Obtain authoritative confirmation, or explicitly authorize a small disposable Developer gateway feasibility deployment/fault test. If native behavior conflicts, approve a revised strict-pinning design before any workaround. |
| B2 | Both Foundries are in West US 3; `x-ms-region` cannot identify A versus B. The docs do not promise that `context.Backend.Id` at every policy stage is the resolved member rather than the pool. | Confirm a supported gateway-derived concrete-member signal, ideally in the same feasibility test, and correlate it with `BackendUrl`/`BackendId` in gateway logs. Do not substitute the client-supplied label or a decoded cookie. |
| B3 | "APIM tracing in Log Analytics" could mean policy trace records or the entire request debugger transcript. Only the first has a verified native workspace destination here. | Confirm that policy-generated traces plus correlated gateway logs satisfy the requirement. If full debug transcripts must be ingested, approve a separate export/ingestion design and its resources/security scope first. |

Task `002` must not begin while these gates remain open. No custom pin token,
response-ID lookup cache, extra trace collector, or live feasibility experiment
is authorized by this document. The options above are decisions, not completed
work or permission to change the agreed requirements.

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

The mini model is a candidate for the scripted conversation; quality has not
been tested and no price/cost ceiling is claimed. Its reasoning and visible
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
| APIM service, APIs, operations, policies, subscriptions, API/service diagnostics | `2024-05-01` | Stable APIM control-plane baseline; S9, S19 |
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
| Pinned backend failure | Preserve the chain and original routing context; no retry onto another backend or history replay. The exact native HTTP/error behavior is **B1**, not a promised 503. |
| Successful HTTP with missing or changed backend evidence | Fail the harness assertion, preserve diagnostics, and stop that chain. A 2xx alone is not an affinity pass. |

### Gateway-derived identity and correlation

The **required** member labels are `backend-a` and `backend-b`. Their mapping
must be generated from the two deployed account hostnames, not model output.
The candidate signal is the effective backend request host after forwarding,
allow-listed against those hosts; **B2 must verify the supported policy signal
and stage** before implementation. Unknown host, pool-only identity, or missing
identity is a failure, never an inferred label. Same-region headers cannot prove
this lab's affinity.

| Name | Owner and contract |
| --- | --- |
| `X-Lab-Session-Id` | Harness-generated UUID, validated at the gateway; correlation label only, not a routing credential |
| `X-Lab-Request-Id` | Gateway overwrites with `context.RequestId`; return on success and policy/error paths where possible |
| `X-Lab-Backend-Id` | Gateway overwrites with verified concrete-member label; absent when no backend was selected rather than inventing one |
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

This is a **lab-only proposal**, not implemented or authorized for execution.
It remains contingent on B1/B2.

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
- First observe the pinned error, then probe fresh sessions. Initial observation
  budget: 180 seconds, at least ten seconds between starts, at most 18 probes.
  Require three consecutive observed healthy-backend results; capture all
  attempts and elapsed time. This is a bounded test criterion, not a provider
  convergence SLA or proof about all future traffic.
- Restore the URL, wait out the actual breaker pause, and observe recovery.
  Failure to establish both backends, unexpected error codes, timeout, missing
  evidence, a changed pin, or failed restoration makes the scenario fail.

An initial backend 404, a documented breaker-open rejection, a pool fallback
attempt, and an unrelated authentication error are different outcomes. Do not
normalize all of them to "successful failover." B1 must determine the permissible
pinned error and prove no unintended member execution.

## 6. Diagnostics, trace destinations, and retention

Select the **native policy-trace path**, subject to interpretation decision B3:
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

Illustrative query shape, **not live-validated evidence**:

```kusto
ApiManagementGatewayLogs
| where TimeGenerated > ago(30m)
| where CorrelationId == "<gateway-request-id>"
| project TimeGenerated, CorrelationId, BackendId, BackendUrl,
          ResponseCode, BackendResponseCode, TraceRecords, Errors
```

Task `007` must verify the request-ID join and inspect real trace record shape,
then turn this into tested queries. Allow up to ten minutes of bounded ingestion
polling; a missing expected record after the budget is a failed check, not proof
that diagnostics work.

## 7. Authentication and least privilege

| Actor/path | Selected access |
| --- | --- |
| Local deployment operator | Existing Azure CLI/azd sign-in to the confirmed target; resource write permissions at the dedicated group and permission to assign the two account-scoped roles. Group creation additionally needs subscription-scope group-create permission. Preflight must verify actual assignments/deny policy; none were changed here. |
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
values. Fault execution defaults to `LAB_ENABLE_FAULTS=false` and requires
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

No experiment was run. If explicitly authorized, use a small, disposable,
dedicated lab deployment with the selected Developer SKU and two backends:

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

Only then close B1/B2, record the decision on B3, and finalize the design before
task `002`. Do not claim completion of task `001` merely because this document
and a research PR exist.

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
