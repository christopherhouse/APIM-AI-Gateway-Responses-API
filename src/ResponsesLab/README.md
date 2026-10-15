# Conversation harness - task 005

**Implemented and offline-tested, not live-verified.** This .NET 10 console
application runs a small developer/architecture-expert script through the
deployed gateway. It is not an agent framework, tool-calling loop, or fault
injector. No real inference or credentials were used for task `005`.

## Build and check without Azure

From the repository root, install the .NET 10 SDK; local checks used `10.0.402`.
The executable uses only .NET libraries (`HttpClient`, `CookieContainer`, and
`System.Text.Json`). Test dependencies are pinned in the xUnit project; first
restore may download public NuGet packages.

```powershell
dotnet build .\ResponsesLab.slnx --configuration Release
dotnet test .\ResponsesLab.slnx --configuration Release --no-restore
dotnet format .\ResponsesLab.slnx --verify-no-changes --no-restore
dotnet publish .\src\ResponsesLab\ResponsesLab.csproj --configuration Release --no-restore --output .\artifacts\publish\ResponsesLab
& .\artifacts\publish\ResponsesLab\ResponsesLab.exe --help
```

The tests replace the HTTP transport with an in-memory handler. They cover
interleaved session isolation, exact response chaining, cookie transport/
expiry, stateless independence, missing/changed member evidence, error and
incomplete response accounting, body limits, request/run deadlines, secret
redaction, and separate distributions. There are no socket calls in tests.
Publishing creates an ignored Windows apphost requiring the .NET 10 runtime;
it does not provision or call Azure.

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

These are two scenarios, not two complete back-to-back runs:

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

## Configuration and limits

The wrapper supplies `LAB_RESPONSES_ENDPOINT`, `LAB_MODEL_DEPLOYMENT`,
`LAB_AFFINITY_COOKIE_NAME`, and `LAB_APIM_SUBSCRIPTION_KEY`. Endpoints must be
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
| `LAB_MAX_RETRIES` | 0 | 0 only; no application retries |
| `LAB_ENABLE_FAULTS` | absent | Absent or `false`; task `006` is not implemented |

Empty, malformed, and out-of-range overrides fail explicitly instead of being
clamped or defaulted. Concurrency is one. Defaults plan **24 requests**:
4 x 3 stateful turns plus 12 independent stateless calls. The maximum
configuration plans **48 requests**, with at most **196,608 output tokens**
requested across the run. These are request/token bounds, not price guarantees;
context input and infrastructure have separate costs.

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
| `scenario`, `sessionId`, `turn`, `prompt` | Synthetic script and independent application session |
| `previousResponseId`, `responseId`, `responseStatus`, `outputText` | Latest chain input, validated response identity/status, and assistant text only |
| `clientRequestId`, `gatewayRequestId` | Separate caller UUID and actual `X-Lab-Request-Id` workspace-correlation UUID |
| `backendId` | Only allow-listed gateway `backend-a`/`backend-b`, never model-authored claims or pool identity |
| `affinityFingerprint` | First 12 SHA-256 hex characters of the opaque cookie, never its value |
| `httpStatus`, `errorCode`, `outcome`, `sent` | Actual response status, safe error code, explicit result, and whether HTTP was attempted |

Success requires completed assistant text, a valid response ID, one gateway
request UUID, a concrete member, unchanged member for an existing stateful
chain, and a usable session-local cookie. Cookies honor domain/path/expiry
through `CookieContainer`; an expired/missing chained cookie fails **before**
another HTTP call. Duplicate/empty/unusable first-turn affinity fails too.
Neither response IDs nor raw cookies are shared across sessions.

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

| Exit | Meaning |
| --- | --- |
| 0 | All planned turns passed and both members were observed in each selected scenario; `--help` is also 0 but sends nothing |
| 1 | Request/contract/affinity/distribution failure, incomplete work, or cancellation/deadline |
| 2 | Invalid invocation or configuration, before HTTP |

Controlled faults are not expected failures in this version; the summary
explicitly reports zero expected-failure scenarios. A 503 during a healthy run
is a failure. Task `006` will add its separately authorized failure procedure,
not reinterpret arbitrary errors as passes.

## What this does not prove

The offline checks and console pass/fail do not establish live provisioning,
native pool behavior, actual model memory/answer quality, APIM policy runtime,
or workspace ingestion. The gateway request UUID is a correlation identifier,
not a full debug trace or proof of a workspace row. Task `007` must run this
exact client against the deployed gateway and correlate actual policy traces,
after the controlled-fault work in task `006`.

## Official references (inspected 2026-10-15)

- [Azure Responses API and chaining](https://learn.microsoft.com/azure/foundry/openai/how-to/responses)
- [Responses create contract, instructions, and output-token bound](https://developers.openai.com/api/reference/resources/responses/methods/create)
- [.NET CookieContainer behavior](https://learn.microsoft.com/dotnet/api/system.net.cookiecontainer?view=net-10.0)
- [Gateway contract and runtime limits](../../infra/policies/README.md)
