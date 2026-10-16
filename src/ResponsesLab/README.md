# Conversation harness - HTTP, Foundry SDK and Agent Framework

**Raw HTTP is live-verified; task `009` SDK/framework modes are offline-tested,
with live proof pending.** This .NET 10 console application runs the same small
developer/architecture-expert script through the deployed gateway using one
selected client/profile per invocation. No tools or model-driven loops are
enabled. Task `006` adds the raw-only guarded endpoint-fault scenario below. No real inference,
credentials, or Azure changes were used for tasks `005`/`006`.
Task `007` separately verified 24 healthy calls and an eight-attempt
failure/restoration/recovery run per environment, with all 64 requests
correlated to actual workspace policy traces. Both labs are now deleted.
See [sanitized results](../../evidence/samples/task007-verification.json).

## Build and check without Azure

From the repository root, install the .NET 10 SDK; local checks used `10.0.402`.
The raw control uses `HttpClient`, `CookieContainer` and `System.Text.Json`.
SDK modes add exact package references and a committed `packages.lock.json`;
test dependencies are pinned in the xUnit project. First restore downloads
public NuGet packages. These are intentionally reviewed pins, not latest-version
claims.

```powershell
dotnet build .\ResponsesLab.slnx --configuration Release
dotnet test .\ResponsesLab.slnx --configuration Release --no-restore
dotnet format .\ResponsesLab.slnx --verify-no-changes --no-restore
dotnet publish .\src\ResponsesLab\ResponsesLab.csproj --configuration Release --no-restore --output .\artifacts\publish\ResponsesLab
& .\artifacts\publish\ResponsesLab\ResponsesLab.exe --help
```

Raw/control and management tests replace HTTP with in-memory handlers. They cover
interleaved session isolation, exact response chaining, cookie transport/
expiry, stateless independence, missing/changed member evidence, error and
incomplete response accounting, body limits, request/run deadlines, secret
redaction, and separate distributions. SDK/framework tests deliberately use
**real SDK calls and a local `127.0.0.1` TCP HTTP server on an ephemeral port**.
They do not replace the stock SDK transport when measuring its defaults.
Only synthetic credentials/responses are used; no Azure sign-in, external model
request, or TLS-validation bypass occurs. Production configuration still
requires HTTPS. Loopback availability is required to run those tests.
Publishing creates an ignored Windows apphost requiring the .NET 10 runtime;
it does not provision or call Azure.
Task `006` adds in-memory scenario/management tests for expected versus unrelated
errors, conservative breaker timing, conditional URL-only writes, ambiguous
write outcomes, concurrent changes, cancellation, restoration, and recovery.

For a reproducible SDK restore check, use:

```powershell
dotnet restore .\src\ResponsesLab\ResponsesLab.csproj --locked-mode
```

The implementation environment could not negotiate TLS with `api.nuget.org`;
the same exact lock was restored through Microsoft's public mirror using
`--source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json`.
No global feed, trust store, or certificate-validation setting was changed.

## Authorized inference through the lifecycle wrapper

**Do not run this example under code-only authorization.** First obtain
approval for the exact lab and real inference, provision it using the
[lifecycle guide](../../scripts/README.md), and publish the client above.

In **PowerShell 7**, use the same environment/subscription/group variables
from that guide:

```powershell
$client = (Resolve-Path .\artifacts\publish\ResponsesLab\ResponsesLab.exe).Path
& .\scripts\Invoke-Lab.ps1 -Action Run -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowCredentialRead -ClientPath $client -ClientArguments @('--allow-requests')
```

The wrapper verifies ownership and fresh deployment outputs, checks API-scoped
access, retrieves the primary gateway subscription key, and supplies it only
through the process environment. It restores previous values/absence even
when this client fails. Do not copy the key into command arguments, JSON
configuration, Bicep parameters/outputs, or `azd env set`.

The client also requires **`--allow-requests`** before reading configuration or
sending anything. With no arguments it exits `2` without HTTP; `--help` exits
`0` without configuration or HTTP. `--scenario both` is the default.
For a separately approved single scenario, pass
`@('--allow-requests', '--scenario', 'stateful')` or the same with `stateless`
as `-ClientArguments`. Invoke the wrapper directly with `&` in PowerShell 7;
do not pass an argument array through a nested `pwsh -File` invocation.

The default `both` selects these two healthy scenarios, not the fault workflow:

| Scenario | Request behavior and assertion |
| --- | --- |
| Stateful | Interleave turns across independent sessions. First turn has no cookie/response ID and `store=true`; later turns send only that session's valid native cookie and latest successful `previous_response_id`. Require the original concrete backend on every turn. |
| Stateless | New UUID for each independent call, `store=false`, no cookie or previous ID; ignore returned cookies. |
| Distribution | Require both `backend-a` and `backend-b` among successful calls separately for each selected scenario. Report actual counts, never assert a perfect split or retry to force one. |

Every request sets `stream=false`, the exported deployment name, bounded
`max_output_tokens`, and reasoning effort. The same expert instructions are
resent on every turn because Responses does not inherit earlier instructions
through `previous_response_id`. Only the latest successful response ID is
retained; the client does not replay full conversation history.

## Client modes and cookie profiles

No new environment variables or deployment settings select a client. Use
`--client` and `--cookie-profile` in the existing wrapper's `-ClientArguments`.
Absent selectors preserve **`http` / `manual`**. SDK modes default to
**`session-scoped`** when the cookie selector is omitted.

| Selector | Concrete implementation | State ownership |
| --- | --- | --- |
| `http` / `manual` | Existing raw request/parser and manual cookie header | Harness advances only its latest successful response ID and owns one jar per `Conversation` |
| `foundry-sdk` / `session-scoped` or `sdk-default` | `ProjectOpenAIClient` → `ProjectResponsesClient.CreateResponseAsync` | Harness supplies the previous successful ID; SDK creates/parses the actual Responses request/result |
| `agent-framework` / `session-scoped` or `sdk-default` | `AsAIAgent` over that same Foundry Responses client | Each conversation reuses an independent `AgentSession`; framework advances service-managed state, not a local chat-history provider |

Pins: `Azure.AI.Extensions.OpenAI` **`3.0.0-beta.3`** (preview),
`Microsoft.Agents.AI.OpenAI` **`1.25.0`**, OpenAI **`2.14.0`**, and locked
System.ClientModel **`1.15.0`**. The Foundry client's supported custom
authentication policy uses the APIM key header and the exported base
`/openai/v1/`; `GetProjectResponsesClientForModel` uses the exported model.
No project discovery, extra route, Foundry project/agent, new role, or direct
backend call is introduced. See the
[resolved compatibility gates and pinned sources](../../docs/PLAN.md#compatibility-gates-before-adapter-implementation).

**Profiles are deliberately different experiments:**

| Profile | Cookie behavior | Expected use |
| --- | --- | --- |
| `manual` | Raw transport disables cookies; harness stores and sends the named native cookie explicitly | Verified control; only accepted with `http` |
| `session-scoped` | Supported custom `HttpClientPipelineTransport`; one `SocketsHttpHandler`/container per conversation, automatic capture/resend only for affinity-on stateful calls | Comparable isolated SDK/framework behavior; not an SDK default |
| `sdk-default` | Leave `Transport` unset; pinned SDK shares an HTTP client and cookie jar across client instances | Observe default behavior; a cookie from one conversation can contaminate another first turn or stateless call |

The SDK adapter does **not** add a manual `Cookie` header. For `sdk-default`
it also does not remove handler-generated cookies or preempt a missing-cookie
continuation with the manual profile's guard. The actual gateway rejection is
reported; a policy error is not an off-mode response-state proof.
The container in a default-profile `Conversation` is observational storage,
not an assertion that the hidden shared handler sent that container's cookie.

Select **one** array below, then pass it to the authorized wrapper command
above. These examples do not authorize live inference:

```powershell
# Isolated SDK healthy run; raw/http uses the default arguments above.
$clientArgs = @('--allow-requests', '--client', 'foundry-sdk', '--cookie-profile', 'session-scoped', '--scenario', 'both')
# Same script with framework-owned service continuation.
$clientArgs = @('--allow-requests', '--client', 'agent-framework', '--cookie-profile', 'session-scoped', '--scenario', 'both')
# Deliberately observe stock cookie sharing; ordinary healthy success is not assumed.
$clientArgs = @('--allow-requests', '--client', 'agent-framework', '--cookie-profile', 'sdk-default', '--scenario', 'both')
# Use only after the approved deployment/export has affinity disabled.
$clientArgs = @('--allow-requests', '--client', 'foundry-sdk', '--cookie-profile', 'session-scoped', '--scenario', 'affinity-off')
```

Change `--scenario` to `stateful` or `stateless` for separately approved
single-scenario runs; use either SDK selector with either SDK cookie profile.
`both`/`stateful` require exported affinity true; `affinity-off` requires false.
No selector changes the deployed pool. SDK modes reject `failure` before
configuration or credentials; the unchanged documented raw fault sequence
remains the only supported fault invocation.

All modes send one current user prompt and the same instructions/options.
For framework continuations, the wire policy checks that the framework's
`previous_response_id` equals the last shared validated response ID. It rejects
conversation-API calls, history replay, a second SDK HTTP call in the same turn,
or any URI/method different from the exact configured Responses endpoint before
sending. Genuine SDK/framework parsing must also succeed and match the shared
ID/text checks. No parse error falls back to raw success.

The [root README scenario walkthroughs](../../README.md#-three-clients-the-same-state-and-routing-rules)
explain the three abstractions, their state/cookie responsibilities and the
off/on/stateless interpretation.

## Configuration and limits

The wrapper supplies `LAB_RESPONSES_ENDPOINT`, `LAB_MODEL_DEPLOYMENT`,
`LAB_AFFINITY_COOKIE_NAME`, `LAB_SESSION_AFFINITY_ENABLED`, and
`LAB_APIM_SUBSCRIPTION_KEY`. Endpoints must be
HTTPS `/openai/v1/responses`, with no userinfo, query, fragment, or custom port.
No endpoint is guessed. The fixed cookie name is `APIM-Backend-Affinity`.

Set optional controls in the local process **before** invoking the wrapper:

| Variable | Default | Accepted bound |
| --- | --- | --- |
| `LAB_STATEFUL_SESSION_COUNT` | 4 | 2-8 |
| `LAB_TURNS_PER_SESSION` | 3 | 2-3; a stateful run must actually chain |
| `LAB_STATELESS_REQUEST_COUNT` | 12 | 2-24 |
| `LAB_MAX_OUTPUT_TOKENS` | 2048 | 256-4096, including reasoning and visible output |
| `LAB_REASONING_EFFORT` | `low` | `low`, `medium`, or `high`; unsupported service combinations fail rather than fall back |
| `LAB_REQUEST_TIMEOUT_SECONDS` | 60 | 5-120, covers response headers and body |
| `LAB_RUN_TIMEOUT_SECONDS` | 600 | 30-1800, includes request spacing and all scenarios |
| `LAB_REQUEST_INTERVAL_SECONDS` | 10 | 10-30 after each completed attempt before the next turn |
| `LAB_MAX_RETRIES` | 0 | 0 only; application and SDK retries disabled |
| `LAB_ENABLE_FAULTS` | absent | Absent or `false` for healthy runs; wrapper supplies `true` only with explicit failure opt-ins |
| `LAB_FAILURE_PROBE_INTERVAL_SECONDS` | 2 | Exactly 2; used only for bounded fault-window checks |

Empty, malformed, and out-of-range overrides fail explicitly instead of being
clamped or defaulted. Concurrency is one. Defaults plan **24 requests**:
4 x 3 stateful turns plus 12 independent stateless calls. The maximum
configuration plans **48 requests**, with at most **196,608 output tokens**
requested across the run. These are request/token bounds; do not extend a
run's authorization implicitly by changing them. This is **one selected
mode/profile**, not this allowance multiplied across all modes/profiles.
The runner sets its planned attempt budget; the SDK before-transport policy
consumes it immediately before sending and rejects any extra per-turn or
run-wide model request. No hidden project or conversation-API request is allowed.

Deadlines can stop a run before all planned work completes. No timeout extends
automatically, no failed chain restarts, and no retry switches backends or
resubmits history. Automatic HTTP redirects are disabled to keep credentials
on the configured endpoint. Response bodies are limited to **1 MiB** with and
without `Content-Length`. Higher effort or insufficient tokens can yield an
incomplete response; that is a failure, not an accepted partial chain.

## Evidence and failure accounting

Each processed turn emits one JSON line with:

| Fields | Meaning |
| --- | --- |
| `clientMode`, `cookieProfile`, `wireAttempts` | Selected stack/profile and actual model transport attempts for this turn (zero or one); the run header lists SDK pins |
| `scenario`, `sessionId`, `turn`, `prompt` | Synthetic script and independent application session |
| `previousResponseId`, `responseId`, `responseStatus`, `outputText` | Latest chain input, validated response identity/status, and assistant text only |
| `clientRequestId`, `gatewayRequestId` | Separate caller UUID and actual `X-Lab-Request-Id` workspace-correlation UUID |
| `backendId` | Only allow-listed gateway `backend-a`/`backend-b`, never model-authored claims or pool identity |
| `affinityFingerprint` | First 12 SHA-256 hex characters of an observed response/session-jar cookie, never its value; not proof of an SDK handler's outbound header |
| `httpStatus`, `errorCode`, `gatewayErrorReason`, `outcome`, `sent` | Actual status, safe error code, fixed gateway breaker reason when present, explicit result, and whether HTTP was attempted |
| `crossResourceStateError` | True only for the exact verified different-resource payload below; does not replace the actual `errorCode` |
| `startedAt`, `finishedAt`, `retryAfterSeconds` | Client-observed timing and optional server pause; not invented server-side breaker timestamps |

Healthy stateful success requires completed assistant text, a valid response ID, one gateway
request UUID, a concrete member, unchanged member for an existing stateful
chain, and a usable session-local cookie. Cookies honor domain/path/expiry
through `CookieContainer`; in manual/session-scoped profiles an expired/missing
chained cookie fails **before** another HTTP call. Duplicate/empty/unusable
first-turn affinity fails too. Those profiles do not share response IDs or
cookies across sessions. SDK-default deliberately preserves the measured shared
transport behavior instead of silently repairing it.

Non-2xx errors retain their HTTP status and optional safe error code; raw error
bodies/messages are not printed. Incomplete output is printed but never advances
the chain. Refusals, malformed/empty output, missing evidence, changed members,
transport failures, and deadlines all fail explicitly. Only assistant
`output_text` is printed, not reasoning items. Known key/cookie values are
redacted even if echoed in model text; JSON encoding escapes control characters.
This is not a general sensitive-content scanner: use synthetic prompts only
and do not enable debug/HTTP tracing or share raw output without review.

The final session table includes outcome, planned/sent/completed/failed/
unattempted turns, affinity violations, and observed backend sets. Separate
stateful/stateless aggregates count successful requests per member, failed
turns, and incomplete sessions. A pre-send cookie failure is a failed processed
turn with `sent=false`; remaining turns of that chain are unattempted.
Cancellation/deadline retains planned sessions in the summary instead of
omitting incomplete work. Other independent sessions continue after an ordinary
chain failure, within the same overall deadline.
`sdkContractViolation` is an explicit pre-send SDK route/body/extra-call/budget
failure, never a repaired success. Response-size checks happen before typed
SDK deserialization; actual received status/gateway headers remain evidence
even when that body is rejected.

| Exit | Meaning |
| --- | --- |
| 0 | All planned turns passed and both members were observed in each selected scenario; `--help` is also 0 but sends nothing |
| 1 | Request/contract/affinity/distribution failure, incomplete work, or cancellation/deadline |
| 2 | Invalid invocation or configuration, before HTTP |

Healthy runs report zero expected-failure scenarios. A 503 during a healthy run
is still a failure. The explicit off-mode comparison accepts only its
cross-backend missing-response error. The separate controlled-failure workflow
accepts only its specific observed fault 404 and gateway breaker rejection.

## Explicit affinity-off comparison

Set the deployment parameter to false, provision, and use
`@('--allow-requests', '--scenario', 'affinity-off')` through the wrapper.
See the [complete walkthrough](../../docs/WALKTHROUGH.md) for both drivers
and safe same-environment mode changes.

The client requires exported `LAB_SESSION_AFFINITY_ENABLED=false`; healthy
stateful/both and controlled-failure invocations require true. Stateless-only
calls work in either mode. Malformed boolean strings are rejected.
The normal defaults and assertions remain unchanged.

The off-mode runner sends up to four three-turn stateful chains sequentially
(12 planned attempts by default), preserves their real previous IDs, and
sends no cookie. A returned affinity cookie is an invalid mode observation,
not something silently discarded. A broken chain stops immediately.
The output still labels requests `stateful`: the chain is stateful even
though the pool's routing is not.
Sequential scheduling models a normal immediate conversation continuation
and avoids accidental alignment between an even interleaved session count
and a pool's selection cycle. It does not force a member or guarantee a switch.

The comparison exits zero only if at least one prior successful chain
receives HTTP 400/404 from the *other* concrete gateway-verified backend,
with a gateway request UUID and matching preceding
response ID; every other chain must complete or meet the same expected-failure
contract. Its error must have either `code=previous_response_not_found`, or
all four fields in this actual, verified service response:

```json
{
  "error": {
    "code": null,
    "type": "invalid_request_error",
    "param": null,
    "message": "The requested item was created under a different Azure OpenAI resource. Use the same resource that created the item to access it."
  }
}
```

The latter sets `crossResourceStateError=true`; the actual code remains null.
Matching is exact, not a substring/category guess. A changed service error
shape fails visibly and needs evidence-backed review, not a broad HTTP-400
exception. No retries or management-plane writes are performed by this mode.
Unrelated errors, policy cookie rejections, missing evidence, deadlines and
successful backend switches remain failures. Accidental same-backend success
without the required error makes the bounded run inconclusive/nonzero.
The final report identifies expected cross-backend failures explicitly.

## Controlled failure and recovery

**Client support:** `http` / `manual` only. SDK/framework fault orchestration
and new live verification are outside task `009`'s current authorization.

**This changes a live backend and sends real inference. Code-only authorization
does not permit running it.** Obtain separate approval for the exact existing
lab, inference, fault, and restoration. Use the same published client and
PowerShell 7 wrapper/target variables as above:

```powershell
$client = (Resolve-Path .\artifacts\publish\ResponsesLab\ResponsesLab.exe).Path
& .\scripts\Invoke-Lab.ps1 -Action Run -EnvironmentName $environment -SubscriptionId $subscription -ResourceGroupName $group -AllowAzure -AllowCredentialRead -AllowFaults -ConfirmResourceGroup $group -ClientPath $client -ClientArguments @('--allow-requests', '--scenario', 'failure', '--allow-faults', '--confirm-resource-group', $group)
```

The argument order is intentional. Missing either fault opt-in, a mismatched
confirmation, or ordinary healthy arguments with `-AllowFaults` fails before
Azure calls. The wrapper holds the existing environment lock, checks permissions
to read/write both exact backend entities, and temporarily supplies
`LAB_ENABLE_FAULTS=true` and the ignored environment's `fault-recovery.json`
path. It restores prior configuration/credentials afterward.

The .NET controller independently validates:

- The explicit active Azure CLI subscription; exact group/APIM/Foundry IDs,
  West US 3 location, environment/owner/purpose tags, Developer SKU/one unit,
  and the actual gateway/Foundry OpenAI endpoint properties.
- Exactly `backend-a` and `backend-b`, their unchanged HTTPS hosts/paths, TLS
  validation, absent backend credentials/proxy, verified breaker parameters,
  and the native two-member equal-weight affinity pool.
- Original backend/pool configurations and concrete ETags. Recheck versions
  before injection; never use wildcard `If-Match`, unconditional PUT, a custom
  fault host, policy-generated 404, or model deletion.

Management uses the existing Azure CLI sign-in, with short-lived ARM tokens
captured in process memory only. CLI children do not inherit the gateway key;
tokens and raw management error bodies are never logged or persisted.
Management HTTP disables redirects/cookies, caps bodies at 1 MiB, and has a
60-second request deadline; each CLI authentication command is bounded to
30 seconds. The pinned backend API remains `2025-03-01-preview`.
The management parser accepts APIM's observed UTF-8 BOM; ownership checks
recognize both `westus3` and ARM's `West US 3` display form without accepting
another or missing region.

### What a passing fault run observes

| Phase | Required observation |
| --- | --- |
| Healthy setup | Observe both members within `LAB_STATEFUL_SESSION_COUNT` attempts, at baseline spacing; retain the first session's original member, response ID, and cookie |
| Apply | Write a non-secret recovery journal before a URL-only conditional PATCH to `/task006-intentionally-missing/openai/v1` on that same host |
| Detect | Up to eight 15-second-spaced observations; successful turns may advance normally while configuration propagates, but require a real forwarded 404 with the original member and valid gateway UUID |
| Fresh sessions | Three consecutive new successful stateful sessions on the other member, two seconds apart |
| Original pin | The same cookie/last successful ID receives 503 with `X-Lab-Error-Reason: CircuitBreakerIsOpen`, a gateway UUID, and no executed-member header; no ID/cookie advancement |
| Restore | Attempt in `finally`, even after cancellation/scenario error; conditional URL-only restoration, unchanged configuration/version checks, and a confirming GET |
| Recover | After the nominal 30-second pause or a longer observed `Retry-After` up to 120 seconds, observe success on the original member using that exact preserved chain; at most eight observations, 15 seconds apart |

The three fresh calls **and** pinned rejection must finish within a conservative
30-second window anchored to the triggering 404 request's **start**, not a new
window starting when its body finishes. Actual latency/propagation can make this
fail. The code does not assert instant convergence or keep trying new sessions
until a convenient sample passes. A longer `Retry-After`, unexpected status,
missing evidence, cookie expiry, backend switch, or exhausted budget fails.

These are named, bounded observations, not application transport retries or
history replay. Only this explicit probe path can continue a chain after the
validated controlled 404/breaker 503; ordinary healthy chains remain terminal
on failure. No arbitrary 401/403/429/500 or unproven 503 becomes an expected pass.

Each attempt emits JSON containing its phase, start/observation times, and the
same safe turn evidence described above. Final phase and session tables retain
HTTP errors, members, counts, separate scenario/restoration errors, and observed
apply/detect/reject/restore/recovery timestamps. Exit `0` requires both
expected-failure assertions, all three healthy fresh sessions, verified
restoration, and original-chain recovery. Otherwise exit `1`.

### Fault budgets and interrupted restoration

The failure scenario is separate from `both`; it does not run the healthy
workloads first. It uses session-count, token/reasoning, request/run deadline,
and baseline-spacing controls above; turns-per-session and stateless-count
controls do not size this workflow. Defaults allow at most **24** HTTP attempts,
or **28** at the maximum setup-session count. At 4,096 output tokens each,
the maximum request budget is **114,688 output tokens**, not a monetary cap.
Early success or failure uses fewer attempts.

The whole-run deadline includes the waits. Restoration gets an independent
**180-second** deadline so Ctrl+C/overall cancellation does not skip cleanup.
Recovery does not extend the overall deadline. Restoration failure is reported
separately and cannot be hidden by a successful scenario assertion.

A hard process kill, machine loss, lost management access, or concurrent edit
can prevent restoration. Before the attempted mutation, the journal is flushed
to disk with only the backend ID, original/fault URLs, original ETag, owner,
original-properties hash, and timestamp. Full backend configuration, credentials,
cookies, and tokens stay out of this file. A verified restore removes it;
an unresolved journal blocks another failure run.
An unchanged GET after an ambiguous/lost apply response does not prove that a
write cannot still complete: the journal is retained and cleanup is reported
unverified. Only a definite conditional rejection or an observed fault followed
by verified restoration resolves it automatically. Unexpected asynchronous
management responses are not treated as completed mutations.

If it remains, **do not delete it merely to rerun or force an unconditional
overwrite**. Stop other writers; obtain authorization for the exact recorded
target; recheck active subscription, ownership, current backend configuration,
and its current ETag. Reconcile any concurrent changes manually. Restore only
the approved original URL with that concrete current `If-Match`, then GET and
verify the intended complete configuration before archiving/removing the
journal. The recorded hash aids review; it is not automatic write authorization.
If those checks cannot be made safely, leave the journal and report the blocker.

## What this does not prove

The offline checks and console pass/fail do not establish live provisioning,
native pool behavior, actual model memory/answer quality, APIM policy runtime,
or workspace ingestion. The gateway request UUID is a correlation identifier,
not a full debug trace or proof of a workspace row. Task `007` ran this
exact client and fault workflow against both deployed gateways and separately
correlated actual policy traces. Generated model explanations can still be
inaccurate; a passing client is not a factual-quality evaluation of its answers.
Offline fault tests are not a new live experiment.

## Official references (inspected 2026-10-15)

- [Azure Responses API and chaining](https://learn.microsoft.com/azure/foundry/openai/how-to/responses)
- [Responses create contract, instructions, and output-token bound](https://developers.openai.com/api/reference/resources/responses/methods/create)
- [.NET CookieContainer behavior](https://learn.microsoft.com/dotnet/api/system.net.cookiecontainer?view=net-10.0)
- [Conditional backend update](https://learn.microsoft.com/rest/api/apimanagement/backend/update)
- [Pinned preview REST specification](https://github.com/Azure/azure-rest-api-specs/blob/main/specification/apimanagement/resource-manager/Microsoft.ApiManagement/ApiManagement/preview/2025-03-01-preview/openapi.json)
- [Gateway contract and runtime limits](../../infra/policies/README.md)
