# Gateway contract - task 003

[`responses.xml`](responses.xml) and [`gateway.bicep`](../gateway.bicep) implement
the gateway configuration. They have **local validation only**. Task `001`'s
separate disposable experiment established the native routing signals; this
expanded policy has not been deployed. No subscription keys are generated
locally, retrieved, or exported by these files.

## One endpoint, two request modes

The only operation is **HTTPS `POST /openai/v1/responses`**. No streaming,
background jobs, Conversations API, retrieval/deletion operations, or wildcard
proxy is provided. This is a synthetic lab contract, not a general-purpose
Responses gateway. Request bodies are inspected with `preserveContent: true`
and otherwise forwarded unchanged; the gateway does not replay history.

| Header | Contract |
| --- | --- |
| `Ocp-Apim-Subscription-Key` | Required API-scoped access; header transport only. Never put keys in URLs. All query parameters, including `subscription-key` and `api-version`, are rejected by the policy. |
| `Content-Type` | `application/json`, optionally with charset |
| `X-Lab-Session-Id` | Required canonical UUID, one value; correlation only, never a routing credential |
| `X-Lab-Scenario` | Required `stateful` or `stateless`; also used for future fault checks using the same request modes |
| `Cookie` | Only the native `APIM-Backend-Affinity` cookie establishes routing affinity. Keep its value opaque and private. |
| `X-Lab-Request-Id`, `X-Lab-Backend-Id` | Caller values are overwritten/deleted; they cannot supply routing evidence. |

Use the deployed `modelDeploymentName` as `model`, and explicitly set
`stream=false`. Other Responses input fields are left to Foundry validation.
The gateway does not impose a separate prompt schema or change model output.

| Mode | Required body and routing context | Client responsibility |
| --- | --- | --- |
| First stateful turn | `store=true`; no `previous_response_id`; no affinity cookie | New isolated cookie container and session UUID |
| Chained stateful turn | `store=true`; nonempty string `previous_response_id`; exactly one nonempty affinity cookie | Replay only that session's cookie and latest successful response ID |
| Stateless call | `store=false`; no `previous_response_id`; no affinity cookie | Independent request; ignore returned `Set-Cookie` |

Omit `previous_response_id` on first/stateless calls rather than sending `null`.
Duplicate affinity cookies, missing/empty chained cookies, a cookie on a first
turn, or routing context on a stateless request produce an explicit **400**.
Ordinary contract errors have `error.code` values such as
`InvalidSessionId`, `InvalidScenario`, `InvalidModel`, `StreamingNotSupported`,
`InvalidStore`, `AffinityCookieRequired`, and `DuplicateAffinityCookie`.
Empty JSON bodies return `InvalidJson`; malformed/non-object JSON instead
raises a native policy-expression error through `on-error`. Its status/body
are gateway-owned, not promised to be a JSON 400.

The policy validates cookie **transport**, not the opaque value's authenticity,
age, ownership, or binding to a response ID. It cannot recover an expired pin.
Clients must honor cookie expiry, discard expired session state, and fail a
chain when the cookie is unavailable. They must also reject missing/changed
backend evidence even after a 2xx. Never restart a lost chain silently, share
cookie containers, forge a pin, or use the session UUID to select a member.
Task `005` implements these assertions in the
[.NET harness](../../src/ResponsesLab/README.md), with offline checks only;
there is no server-side pin store.

## Native routing and failure

Both member URLs come from Foundry's returned OpenAI endpoint property plus
`openai/v1`; the operation supplies `/responses`. The native `responses-pool`
has equal weights/priorities and cookie source **`cookie`**, with fixed name
`APIM-Backend-Affinity`. Small samples need not be evenly distributed.

Each `backend-a`/`backend-b` breaker counts one `404`, `429`, or `500-599`
within `PT1M`, with `PT30S` trip duration and `acceptRetryAfter=true`. Detection
is approximate and gateway-local; `Retry-After` can lengthen the pause.
The policy forwards **once**, waits at most 60 seconds for backend response
headers, does not follow redirects, and adds no retry/fallback/history replay.
The client still needs an end-to-end deadline.

A real backend HTTP error keeps its original status/body, including a non-JSON
body, and receives concrete-member evidence after forwarding. A native
breaker-open rejection retains the gateway's failure status/body and omits
executed-member evidence. Task `001` observed 503 / `CircuitBreakerIsOpen`
without forwarding for the original cookie, while fresh sessions reached the
healthy member inside the open window. This is not a promise of indefinite
healthy-only routing or a global availability SLA. Controlled fault scripts
are implemented by task `006` in the
[guarded fault workflow](../../src/ResponsesLab/README.md#controlled-failure-and-recovery);
they have not been live-verified with this policy.

## Trustworthy evidence and credentials

`X-Lab-Request-Id` is `context.RequestId`, returned on successful forwarding,
contract rejections, and `on-error` where policy execution is reached.
Core gateway errors before API policy execution may not carry it.

After forwarding, `X-Lab-Backend-Id` is derived **only** by matching effective
`context.Request.Url.Host` against the two deployed hosts. A match gives
`backend-a` or `backend-b`; an unknown host produces **502 / UnresolvedBackend**
instead of a successful response with guessed evidence. No host/context/debug
probe headers are returned. `context.Backend.Id` is not used as the member.
The error path deletes the member header rather than implying execution.

Task `006` adds `X-Lab-Error-Reason: CircuitBreakerIsOpen` only when
`context.LastError.Reason` is exactly that native gateway error. The policy
deletes caller/backend copies in inbound, outbound, and error paths; it never
copies arbitrary error text or invents an executed member. The fault harness
requires this reason plus 503 and a gateway UUID for its pinned-breaker
assertion. A bare 503 is not sufficient proof. The header is not a full trace;
workspace correlation of the exact deployed policy remains task `007`.

APIM subscription access is independent of backend identity. Caller
`Ocp-Apim-Subscription-Key`, `api-key`, and `Authorization` are removed before
forwarding. `authentication-managed-identity` supplies the gateway credential
for `https://cognitiveservices.azure.com`, failing explicitly if it cannot.
Both account-scoped OpenAI User grants come from the foundation. Backend TLS
chain/name verification stays enabled; Foundry key auth stays disabled.
The API-scoped `lab-access` subscription disallows subscription-based tracing.
Task `004` implements key retrieval/process-environment handoff; no keys appear
in Bicep outputs or `.bicepparam`.

## Native workspace traces

The API diagnostic references an explicit **`azuremonitor` logger**. Together
with the foundation's `GatewayLogs` diagnostic setting, it selects
`ApiManagementGatewayLogs` and native `TraceRecords`: 100% fixed sampling,
`information` verbosity, all errors, no client-IP capture, and zero frontend/
backend request/response body bytes. Request/backend header capture is empty;
only gateway-written `X-Lab-Request-Id` and `X-Lab-Backend-Id` response headers
are allow-listed.

The `responses-lab` policy source emits compact inbound/outbound/rejection/
error messages. Validated session/scenario labels appear on normal traffic;
errors before validation omit untrusted labels. All messages include the
gateway request UUID. Forwarded outcomes also include member and HTTP status;
gateway errors use `LastError.Reason`, never the potentially sensitive message.
No prompts, completions, response IDs, keys, bearer tokens, cookies, raw URLs,
or payloads are added to these traces. Resource diagnostics can still contain
service metadata: use synthetic traffic only and inspect fields in task `007`.

After an authorized deployment and traffic, substitute a returned request ID:

```kusto
ApiManagementGatewayLogs
| where TimeGenerated > ago(30m)
| where CorrelationId == "<X-Lab-Request-Id>"
| project TimeGenerated, CorrelationId, BackendId, BackendUrl,
          ResponseCode, BackendResponseCode, LastErrorReason, TraceRecords
```

Nonempty policy trace content and correlation must both be verified; a
response header alone does not prove ingestion. Allow up to ten minutes for
bounded ingestion checks, then fail missing evidence. Full debugger transcript
ingestion is not configured or claimed. See the
[design and feasibility evidence](../../docs/DESIGN.md).

## Local versus live checks

Use the commands in the [infrastructure reference](../README.md#local-validation-no-azure-sign-in-required).
Node tests inspect compiled resource/dependency/auth/diagnostic contracts.
The PowerShell test parses XML, compiles the actual expressions as C# 7 against
small context stand-ins and PowerShell's bundled Newtonsoft, and executes
first/chained/stateless, invalid-input/cookie, and host-evidence fixtures.
It is **not** an APIM emulator, gateway policy validator, or ARM interpreter.

Task `007` must deploy the exact expanded policy and prove request path/body
preservation, subscription enforcement/secret stripping, identity propagation,
opaque-cookie behavior, member evidence, real HTTP errors, native breaker
rejection/recovery, policy rejection/error correlation, and workspace
ingestion/privacy. No local check substitutes for those live assertions.

## Official implementation sources (inspected 2026-10-15)

- [Pinned backend/pool schema](https://learn.microsoft.com/azure/templates/microsoft.apimanagement/2025-03-01-preview/service/backends)
- [Backends, load balancing, affinity, and breakers](https://learn.microsoft.com/azure/api-management/backends)
- [Policy expressions and supported types](https://learn.microsoft.com/azure/api-management/api-management-policy-expressions)
- [Forwarding and error/redirect behavior](https://learn.microsoft.com/azure/api-management/forward-request-policy)
- [Native trace table fields](https://learn.microsoft.com/azure/azure-monitor/reference/tables/apimanagementgatewaylogs)
- [Diagnostic controls](https://learn.microsoft.com/azure/templates/microsoft.apimanagement/2024-05-01/service/apis/diagnostics)
- [Managed identity policy](https://learn.microsoft.com/azure/api-management/authentication-managed-identity-policy)
