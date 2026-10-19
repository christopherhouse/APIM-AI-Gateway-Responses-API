# Stateful Responses API gateway lab plan

## Scope and authorization

This records the agreed implementation plan and authorization history.
Tasks `000`-`008` are complete. Task `008` delivery, live verification and its
README/code-citation follow-up are published in PR #10, merged on 2026-10-16
at `99cd7b0`. On that date, the user requested
an integrated cookie/response-ID explanation with code excerpts and source-line
citations. That earlier follow-up was documentation-only: no new Azure operations,
application changes, or cost documentation.
Later on the same date, the user authorized writing up a proposed Agent
Framework and Foundry SDK harness comparison on
`feature/008-self-service-affinity-toggle` before merging PR #10.
That planning follow-up is included in PR #10. After its merge, the user
authorized building task `009` and requested a full README explanation of
each scenario. The code, dependencies, offline checks and scenario documentation
were delivered on `feature/009-sdk-framework-comparison` in PR #11, merged on
2026-10-19 at `24754e8`. On that date, the user authorized live Azure testing.
Preparation continues on `feature/009-sdk-live-verification`; task `009` is
`blocked` because its former disposable lab is confirmed deleted and no active
target is configured. The preceding testing scope explicitly excluded
infrastructure changes. Replacement provisioning, native affinity updates and
scoped cleanup therefore need exact-target/action approval before execution.
No live SDK inference or resource mutation has occurred.
Task `008` was authorized on 2026-10-16 after
PR #9 merged. Work on `feature/008-self-service-affinity-toggle` excludes cost
documentation at the user's request. Add a default-on native backend-pool
session-affinity parameter and compare unpinned cross-backend response-chain
failure against pinned success. After the scoped handoff, the user approved
the new `walk008` / `rg-responses-walk008` environment in West US 3 in the same
active subscription: provisioning, bounded off/on inference, affinity-only
redeployments, driver parity/reruns, evidence checks and exact owned-group
deletion without purge. Limit execution to 64 attempts at 2,048 output tokens
each with no transport retries; no existing application changes are authorized.
The implemented toggle and clean walkthrough have now passed live verification:
four unpinned cross-resource failures, 24 successful pinned/stateless requests,
all 42 request/policy-trace joins including diagnostics, an unchanged rerun
preserving 13 runtime values, and verified scoped deletion without purge.
Task `008` live verification is complete in merged PR #10; see
[sanitized results](../evidence/samples/task008-verification.json).
On 2026-10-15, the user authorized task `000` after planning PR #1 was merged,
then task `001` after bootstrap PR #2 was merged. That authorization covered
platform design and its read-only checks. Later on 2026-10-15 the user explicitly
authorized a cost-bounded, disposable task `001` feasibility deployment,
controlled failure, evidence collection, and cleanup. This exception does not
authorize tasks `002`-`008` or the complete self-service lab. After merging
PR #3 on the same date, the user separately authorized task `002` code, local
validation, documentation, and PR publication. No new live Azure operations
are authorized by that request. After PR #4 merged on 2026-10-15, the user
authorized the next task, `003`, for code, local checks, documentation, and PR
publication only. No new live Azure operations are authorized.
After PR #5 merged on the same date, the user authorized task `004` deployment
orchestration code, offline checks, documentation, and PR publication only.
This does not authorize running that orchestration against Azure.
After PR #6 merged, the user authorized task `005` harness code, offline
validation, documentation, and PR publication only. No live Azure operations,
including inference or credential retrieval, are authorized.
After PR #7 merged, the user authorized task `006` controlled-failure/recovery
code, offline checks, documentation, and PR publication only. This does not
authorize Azure reads, real inference, or injecting/restoring a live fault.
After PR #8 merged, the user approved starting task `007` live verification
on 2026-10-15. Preparation uses the active subscription and West US 3; exact
new-resource targets and provisioning/fault/cleanup actions are confirmed
before execution. At 16:31 CDT on the same date, after the exact target/action
handoff, the user explicitly instructed autonomous continuation. This authorizes
the new `rg-responses-verify007-azd` and `rg-responses-verify007-cli` groups in
the unchanged active subscription / West US 3, bounded inference, reversible
fault/restoration, unchanged reruns, and scoped cleanup. Task `008` is not included.
Before service provisioning, the user additionally required a dedicated
user-assigned managed identity for APIM. Include that new owned identity in
each target group; explicitly select its client ID in the gateway policy and
retain only the two Foundry-scoped OpenAI User grants.

The lab demonstrates conversation-aware routing through Azure API Management
(APIM) to two Microsoft Foundry backends. Conversations chain Responses API
calls using `previous_response_id`; the demonstration must prove that their
requests stay on the appropriate backend.

"Stateful APIM" describes the routing experience, not a promise that APIM stores
the model's conversation history. The design must explain separately where
response state lives, how affinity is established, and how the client preserves
the required routing information.

## Agreed requirements

| Area | Requirement |
| --- | --- |
| Azure context | Use the active Azure CLI subscription; display and record the resolved subscription before deployment. |
| Resource group | Create a new dedicated lab resource group through orchestration or Azure CLI, outside Bicep ownership. |
| Region | West US 3 (`westus3`) for all regional resources. Do not silently substitute another region. |
| Gateway | New Developer SKU APIM instance. |
| Model backends | Two new Foundry resources, each with its own deployment of the same model and version. |
| Model choice | User requires GPT-5-family or newer. Task `001` verified `gpt-5.4-mini` / `2026-03-17` / `GlobalStandard` with real Responses calls; West US 3 resources, global inference processing. |
| Networking | Public network access enabled; authentication remains required. No existing application resources are reused. |
| Infrastructure | Bicep with native `.bicepparam` deployment inputs, plus APIM policy XML where needed; no maintained JSON parameter files. |
| Routing | Backend load-balancing pool, per-backend circuit breakers, and a default-on deployment parameter for the documented native session-affinity mechanism. |
| Stateless traffic | Demonstrate distribution across both identical model deployments without conversation chaining. |
| Stateful traffic | Multiple independent conversations, each chaining with `previous_response_id` and retaining affinity. |
| Failure behavior | A conversation pinned to an unavailable backend fails explicitly; new conversations use a healthy backend once the breaker detects the failure. |
| Harness | One .NET 10 console application: live-verified raw HTTP control plus task `009` offline-tested Foundry SDK/Agent Framework modes and explicit cookie profiles. No tools or model-driven loops; SDK live proof pending. |
| Conversation script | A developer asks an Azure architecture expert about making APIM routing stateful for Responses. |
| Console evidence | Model output plus session ID, backend identity, and trace/correlation identifiers for every turn. |
| Summary evidence | Each session's backend set, turn counts, outcomes, affinity violations, and aggregate backend distribution. |
| Observability | Bicep-deployed Log Analytics workspace; diagnostics from every resource that supports them; APIM tracing evidence queryable there. |
| Delivery | Self-service lab with a thorough README, `azd` deployment orchestration, equivalent Azure CLI instructions, and teardown. |

## Intended architecture

```text
.NET 10 console harness
  | independent sessions + previous_response_id + affinity information
  v
APIM Developer
  | inbound/outbound policies and diagnostic correlation
  v
Backend load-balancing pool
  +-- Backend A + circuit breaker --> Foundry A / model deployment
  +-- Backend B + circuit breaker --> Foundry B / same model deployment

Supported resource diagnostics and gateway evidence --> Log Analytics
```

This is a logical overview of the implemented system. [DESIGN.md](DESIGN.md)
records task `001` source-backed choices and live disposable feasibility
evidence as of 2026-10-15. B1/B2 are resolved: native pinned-breaker rejection
and concrete outbound host evidence passed. Task `002` implements the
infrastructure foundation in merged PR #4. Task `003` adds gateway routing,
affinity, and diagnostic policies on `feature/003-gateway-routing-policies`.
Task `004` implements guarded azd/CLI orchestration for those templates;
both drivers share native `deploy.bicepparam` inputs and external group
ownership. These were code-only deliveries; runtime evidence was not claimed
until the separately authorized task `007`.
Task `005` adds the bounded .NET 10 client, isolated response chains, and
per-turn/session evidence; its HTTP transport tests are offline only.
Task `006` adds the opt-in same-host backend-path fault, bounded breaker/
fresh-session observations, conditional restoration, and original-chain recovery.
Task `007` subsequently exercised both healthy and fault workflows against
clean azd and CLI environments, including actual workspace policy traces,
unchanged reruns, and verified scoped cleanup. Those results are separate from
the earlier tasks' offline checks.
The selected trace contract is verified native policy trace records plus
correlated gateway logs, not full debugger transcripts.

## Mandatory design gates

Task `001` must resolve these points with current official documentation and
subscription evidence before implementation proceeds:

1. Verify Developer SKU support for the chosen backend pool, affinity, circuit
   breaker, policy, and diagnostic features. Record any limitations.
2. Verify the Foundry resource type, Responses endpoint and API version, model,
   deployment type, availability in West US 3, quota, and access requirements.
3. Establish the recommended affinity contract: how a new session selects a
   backend, how the gateway returns affinity information, and how the harness
   carries it forward. Do not invent a response-ID mapping/cache unless the
   verified design requires one and its additional scope is approved.
4. Establish the routing contract when an affinity-bound backend is unhealthy.
   Do not assume the pool preserves a pin after breaker activation. If the
   native behavior conflicts with explicit failure, document the gap and
   obtain a decision before implementing a workaround.
5. Distinguish gateway access/resource logs, policy-generated trace records,
   and per-request APIM debug traces. Verify which can reach Log Analytics
   natively and whether an additional supported component is required. Do not
   describe correlation IDs or access logs alone as full APIM traces.
6. Inventory diagnostic support by deployed resource type. Route every supported
   category to the workspace; document resources that cannot emit diagnostic
   settings and any gaps against the requested tracing experience.
7. Select and document local/deployment/runtime authentication, least-privilege
   role assignments, secret handling, and APIM subscription access. Public
   endpoints must not become anonymous endpoints.
8. Verify how `azd` creates or uses the resource group without Bicep managing its
   lifecycle, and ensure the Azure CLI path uses the same Bicep and parameters.

A failed or unresolved gate is a documented blocker, not permission to change
the requested region, SKU, failure semantics, or diagnostic requirements
silently. Current gate results and the exact conditions to resume are recorded
in [DESIGN.md](DESIGN.md).

## Harness and evidence contract

- Assign a harness session ID before each conversation and keep it separate
  from response IDs and any gateway-native affinity token.
- Retain only that session's latest response ID and routing context. Never
  share response IDs or affinity state across sessions.
- Print each turn's prompt/response, backend identity, session ID, response ID,
  previous response ID, request correlation, and outcome. Redact credentials
  and sensitive affinity material; use a safe identifier or fingerprint.
- Derive backend evidence from gateway routing, not model-generated claims.
- Treat an unexpected backend switch in a chained conversation as a failed
  assertion, even if the HTTP request succeeds.
- Run sufficient independent sessions to observe both backends. Do not promise
  a perfectly even split from a small sample or depend on undocumented
  round-robin behavior.
- Report stateless distribution separately from stateful conversation affinity.
- Produce a final per-session table and aggregate statistics, including failed
  requests and incomplete conversations. Return a failing exit status when
  expected routing or failure assertions are violated.
- Make expected scenario failures distinguishable from test failures. A
  deliberately unavailable pinned backend is a successful failure scenario only
  when the expected error and new-session behavior are both demonstrated.

## Failure demonstration

Establish successful sessions first, identify one pinned backend, then introduce
a controlled and reversible failure using a mechanism selected in task `001`.
Observe the documented breaker detection/convergence window rather than
assuming immediate activation.

The pinned conversation must fail explicitly, without silently restarting its
history or continuing on the other backend. After the breaker has detected the
failure, fresh sessions must reach the healthy backend. Capture the evidence,
restore the backend, and demonstrate recovery. Put guardrails around the
dedicated lab resources so the fault procedure cannot target unrelated resources.

## Delivery and lifecycle

The README must explain the state boundary, affinity contract, load balancing,
circuit breakers, failure behavior, observability, authentication, prerequisites,
deployment duration expectations, and known limitations. Task `008` explicitly
excludes cost documentation.

Both deployment paths must include prerequisite checks, subscription
confirmation, resource-group creation outside Bicep, configuration, deployment,
harness execution, evidence queries, troubleshooting, and scoped teardown.
Document whether `azd down` removes the externally created group or whether
explicit group deletion is required; avoid orphaned resources.

Keep workloads, capacity, and token budgets small and configurable. Document
cleanup responsibilities without adding cost estimates or pricing guidance.

## Repository workflow

Task `000` must codify the following in `.github\copilot-instructions.md`:

- No work directly on `main`.
- Use descriptive `feature/*`, `chore/*`, `bugfix/*`, or other suitable work branches.
- Keep each branch focused on a coherent task or documented group of tasks.
- Update task/progress records with the implementation branch.
- Complete relevant checks and create a pull request when branch work is done.
- Do not merge pull requests; leave review and merging to the user.
- Do not build or deploy beyond the current authorization.
- Never commit credentials, generated secrets, or private environment settings.

The repository's initial base was created with explicit user approval on
2026-10-15 at empty initialization commit `846a2cb`. Planning PR #1 was merged
into `main` at `f5bae67` on the same date. Task `000` was then explicitly
authorized on `chore/000-repository-bootstrap`; no implementation work is
permitted directly on `main`.

Bootstrap PR #2 was merged at `9efe056` on 2026-10-15. Task `001` then began on
`chore/001-platform-design`. PR #3 merged at `e1c4ca0` on 2026-10-15 after the
feasibility experiment and cleanup completed. Task `002` then began on
`feature/002-infrastructure-foundation` with separate code-only authorization.
PR #4 merged at `2967f55` on 2026-10-15. Task `003` then began on
`feature/003-gateway-routing-policies` with code-only authorization.
Its implementation and local validation are complete in PR #5, merged at
`e1ed64a` on 2026-10-15. Task `004` began on
`feature/004-deployment-orchestration` with code-only authorization.
Its implementation and offline validation are complete in PR #6, merged at
`a74ebd2` on 2026-10-15. Task `005` began on
`feature/005-conversation-harness` with code-only authorization.
Its implementation and offline validation are complete in PR #7, merged at
`dadd598` on 2026-10-15. Task `006` began on
`feature/006-controlled-failure-recovery` with code-only authorization.
Its implementation and offline validation are complete in PR #8, merged at
`721d299` on 2026-10-15. Task `007` began on
`feature/007-live-verification` after approval to start live verification.
Read-only preparation and cold validation corrected real lifecycle contracts;
PR #9 preserves those fixes and the dedicated user-assigned identity.
Both clean labs passed provisioning, healthy routing, controlled failure/
recovery, correlated workspace diagnostics, unchanged reruns, and verified
scoped teardown. Initial CLI deployment polling and azd teardown errors were
reconciled through actual Azure state checks, not treated as successful command
exits. Recovery records and associated model quota remain without purge.
Task `007` is complete in PR #9, merged at `e66815a` on 2026-10-16.
Task `008` is complete in merged PR #10 on
`feature/008-self-service-affinity-toggle`, including the documentation-only
follow-ups. The live authorization above was exercised and cleanup verified;
it does not authorize task `009`.

## SDK and Agent Framework comparison

**Status: task `009` blocked on a live target; no SDK live verification yet.** The user
authorized this writeup before merging PR #10, then separately approved SDK/
framework implementation and scenario documentation after its merge on
2026-10-16. Implementation used `feature/009-sdk-framework-comparison` from `99cd7b0`.
The compatibility gates below are resolved for the pinned stack; adapters and
focused real-SDK loopback tests are implemented. Code/offline delivery in PR #11
merged on 2026-10-19. Live testing and target/telemetry reads were authorized
that day, but the former lab no longer exists. The continuation branch is
`feature/009-sdk-live-verification`. Replacement provisioning and affinity
updates are not inferred from testing permission; live off/on/workspace
acceptance remains pending that target authorization.

### One harness, three client modes

The existing raw HTTP path remains the control. Small adapters live
within the same .NET harness rather than three applications and reuse the prompts,
interleaved schedule, request bounds, evidence and outcome assertions.
These are implemented command-line selectors; no option runs all modes at once:

| Mode | Implemented conversation handling | Routing behavior to measure live |
| --- | --- | --- |
| `http` | Existing explicit response-ID chain and per-conversation cookie storage | Current manual `Cookie` header path remains the verified control |
| `foundry-sdk` | `ProjectResponsesClient.CreateResponseAsync` from `Azure.AI.Extensions.OpenAI` | Compare default shared cookie capture/resend with explicitly configured automatic session-scoped transport |
| `agent-framework` | `AsAIAgent` over that same Foundry client, independent reused `AgentSession`, service-managed Responses IDs | Same two cookie profiles; framework memory alone does not isolate HTTP cookies |

These are abstraction levels, not necessarily three independent transports.
Record the actual framework/provider/SDK stack. The comparison must expose
who advances the service response/conversation ID and who captures and resends
the native affinity cookie; an application session UUID remains correlation,
not a routing pin.

### Compatibility gates before adapter implementation

Resolved on 2026-10-16 by exact package source inspection, dependency restore/
compilation and real SDK/framework requests to a synthetic loopback server:

| Gate | Decision and local evidence |
| --- | --- |
| Concrete stack | Exact direct pins: `Azure.AI.Extensions.OpenAI` `3.0.0-beta.3`, `Microsoft.Agents.AI.OpenAI` `1.25.0`, OpenAI `2.14.0`; committed lock also fixes System.ClientModel `1.15.0` and Microsoft.Extensions.AI.OpenAI `10.10.1`. Foundry/Responses preview API opt-in is limited to the adapter's `OPENAI001` pragma. |
| Endpoint/authentication | Public `ProjectOpenAIClient(AuthenticationPolicy, ProjectOpenAIClientOptions)` uses the configured endpoint directly. Set base `/openai/v1/`, use `ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy` for the APIM subscription header and call `GetProjectResponsesClientForModel`. Leave agent name unset: no project prefix, discovery, conversation API, dated query, backend token, new project, route or role. Wire tests verify exact POST path and header. APIM's existing managed identity still authenticates downstream. |
| Framework state | `AsAIAgent(ChatClientAgentOptions)` wraps the Foundry Responses client. Independent `AgentSession` instances advance service-managed response IDs; stored output is enabled for stateful calls. No `ChatHistoryProvider`, tools or automatic function-invocation wrapper. Actual interleaved requests contain the preceding response ID and exactly one current user message; the adapter never sets the session's internal conversation ID. |
| Cookies | Stock System.ClientModel transport uses a shared `HttpClientHandler`/cookie jar, with redirects disabled. Default-profile loopback requests prove automatic cookie capture/resend across unrelated sessions and stateless calls. Configured profile supplies one `SocketsHttpHandler`/container/transport per conversation; automatic cookies only for affinity-on stateful calls. Native multi-value headers are preserved, including duplicate-cookie rejection. |
| Bounds and response handling | Zero SDK retries plus exact-route, one-call-per-turn and planned run-wide guards before transport. Bounded 1 MiB observation precedes genuine typed parsing; shared validation checks parsed ID/text, gateway evidence and member. HTTP errors retain actual status/code, while typed/schema failures cannot become raw-JSON success. Extra calls, replay or route changes fail explicitly. |

Implementation: [`SdkResponseSender.cs`](../src/ResponsesLab/SdkResponseSender.cs),
shared [`ResponsesClient.cs`](../src/ResponsesLab/ResponsesClient.cs),
[`ClientSelection.cs`](../src/ResponsesLab/ClientSelection.cs) and
[`SdkClientTests.cs`](../tests/ResponsesLab.Tests/SdkClientTests.cs).
No topology expansion was required or made. This closes local compatibility
gates, not the live behavior assertions below.

### Default behavior versus configured behavior

For each SDK/framework mode, report two distinct profiles:

- **SDK default cookies:** configure the necessary endpoint, authentication,
  lab/correlation headers, timeouts and disabled retries, but leave cookie
  behavior at the pinned SDK's default. Observe cookie capture, resend and
  sharing without adding the control client's manual cookie forwarding.
  A failure or globally shared pin is a recorded finding, not a successful
  session-isolation demonstration.
- **Session-scoped cookies:** if the default is insufficient, demonstrate
  supported configuration that gives each conversation its own cookie context.
  Prefer automatic handler capture/resend when supported; otherwise label any
  explicit transport/pipeline policy as custom cookie handling. Do not describe
  that configured result as out-of-the-box SDK behavior.

The adapter boundary must preserve each client's real SDK/framework response
handling. Reuse the evidence/assertion logic, not a hidden raw-HTTP replacement.
Expose the selected mode, pinned versions and cookie profile in sanitized
results. No cookie values, credentials or raw request bodies enter diagnostics.

### Comparable proof and staged authorization

Implemented focused offline transport tests under the code authorization:
cookie issuance/resend, interleaved session isolation, response-ID advancement,
stateless calls without routing state, missing/duplicate/expired cookies,
HTTP failures, unexpected backend changes, cancellation and request bounds.
The tests assert wire-level continuation fields and absence of history replay
using in-memory synthetic observations; raw bodies are not persisted as evidence.

Disable SDK/provider/transport retries and count actual model HTTP attempts,
not just `RunAsync` or SDK method calls. Account for extra framework/SDK calls;
the existing 24-request default must not silently become 24 requests per mode
or per profile. Stop before exceeding the approved run-wide request/token/
time budget and report failures and unattempted work explicitly.

Live proof requires a separate exact-target/action and aggregate-budget
approval. For each supported mode, use the same model/topology, prompts and
interleaving: four independent three-turn chains plus 12 separate stateless
calls, with profiles/runs explicitly budgeted. Compare native affinity
off against on. An off-mode pass still requires an actual cross-member
backend response-state error using a real prior ID, not a missing-cookie
policy rejection, local guard, generic HTTP error or accidental same-member
success. The default-cookie profile may be incompatible with the lab contract;
record that result separately and use a labeled supported profile for the
comparable end-to-end proof.

For on-mode success, require zero backend switches, independent session
routing contexts, observations of both members across independent stateful
chains and separately across stateless calls, and request/member/status
matches to actual workspace policy traces. Do not promise an exact split.
Cookie replay alone, model text and one shared global pin are insufficient
evidence. Retain the existing
raw-mode regression and explicit-error/no-replay behavior; new controlled
faults are not included without separate approval.

Publish exact configuration, required customizations, observed default
behavior, SDK/framework limitations and redacted results. Offline checks
cannot establish live routing. If a gate or live approval remains unresolved,
leave the corresponding acceptance item pending/blocked rather than claiming
task `009` complete. Add no new resource, SDK or Azure-operation claim to the
current task `008` evidence.

### Primary references for the compatibility gates

Reviewed 2026-10-16; these establish API/state boundaries, not SDK affinity
verification against a live Azure environment:

- [Foundry client at the package's exact source commit](https://github.com/Azure/azure-sdk-for-net/blob/799dc2d97ee2d19da9f7e2eb54e7e4f6f3e2d21e/sdk/ai/Azure.AI.Extensions.OpenAI/src/Custom/OpenAI/ProjectOpenAIClient.cs):
  public custom-auth constructor, direct endpoint and model Responses client.
- [Agent Framework `1.25.0` Responses extension](https://github.com/microsoft/agent-framework/blob/dotnet-1.25.0/dotnet/src/Microsoft.Agents.AI.OpenAI/Extensions/OpenAIResponseClientExtensions.cs):
  `AsAIAgent` and its underlying `AsIChatClient` provider.
- [Exact restored Microsoft.Extensions.AI.OpenAI source](https://github.com/dotnet/extensions/blob/59e1741f6fb45cf7ae08e2df088aef71537a7bc4/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIResponsesChatClient.cs):
  stored response ID becomes the service conversation ID; subsequent non-conversation
  IDs become `PreviousResponseId`; current input is appended to the SDK options.
- [System.ClientModel `1.15.0` default HTTP transport](https://github.com/Azure/azure-sdk-for-net/blob/System.ClientModel_1.15.0/sdk/core/System.ClientModel/src/Pipeline/HttpClientPipelineTransport.cs):
  shared default client/handler, automatic cookie defaults and disabled redirects.
- [System.ClientModel supported pipeline/transport configuration](https://github.com/Azure/azure-sdk-for-net/blob/System.ClientModel_1.15.0/sdk/core/System.ClientModel/samples/Configuration.md):
  `BeforeTransport` policies and `HttpClientPipelineTransport(HttpClient)`.

- [Microsoft Foundry SDKs and endpoints](https://learn.microsoft.com/en-us/azure/foundry/how-to/develop/sdk-overview):
  distinguishes project and OpenAI-compatible clients and project routes.
- [Agent Framework conversations and sessions](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/):
  describes session reuse between invocations.
- [Agent Framework storage](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/storage):
  distinguishes service-managed storage from local message history.
- [APIM session awareness and cookie management](https://learn.microsoft.com/en-us/azure/api-management/backends#session-awareness):
  requires the client to retain and return the appropriate routing cookie.

## Execution and progress

Use [TASKS.md](TASKS.md) as the authoritative task order and acceptance checklist.
Use [PROGRESS.md](PROGRESS.md) as the current handoff and append-only execution
log. Task `000` is the first implementation task; planning preparation does not
count as completing bootstrap.
