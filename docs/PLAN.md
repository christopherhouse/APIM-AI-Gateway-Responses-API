# Stateful Responses API gateway lab plan

## Scope and authorization

This is the agreed implementation plan, not an implemented solution.
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
| Routing | Backend load-balancing pool, per-backend circuit breakers, and the documented session-affinity mechanism verified during design. |
| Stateless traffic | Demonstrate distribution across both identical model deployments without conversation chaining. |
| Stateful traffic | Multiple independent conversations, each chaining with `previous_response_id` and retaining affinity. |
| Failure behavior | A conversation pinned to an unavailable backend fails explicitly; new conversations use a healthy backend once the breaker detects the failure. |
| Harness | .NET 10 console application; scripted multi-turn loops, not an agent framework or tool-calling system. |
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

This is a logical design, not an implemented system. [DESIGN.md](DESIGN.md)
records task `001` source-backed choices and live disposable feasibility
evidence as of 2026-10-15. B1/B2 are resolved: native pinned-breaker rejection
and concrete outbound host evidence passed. Task `002` implements the
infrastructure foundation in merged PR #4. Task `003` adds gateway routing,
affinity, and diagnostic policies on `feature/003-gateway-routing-policies`.
Task `004` implements guarded azd/CLI orchestration for those templates;
both drivers share native `deploy.bicepparam` inputs and external group
ownership. This is offline-validated code, not a deployed self-service lab.
Task `005` adds the bounded .NET 10 client, isolated response chains, and
per-turn/session evidence; its HTTP transport tests are offline only.
Task `006` adds the opt-in same-host backend-path fault, bounded breaker/
fresh-session observations, conditional restoration, and original-chain recovery.
It too is offline-validated code, not live fault/routing evidence.
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
cost drivers, deployment duration expectations, and known limitations.

Both deployment paths must include prerequisite checks, subscription
confirmation, resource-group creation outside Bicep, configuration, deployment,
harness execution, evidence queries, troubleshooting, and scoped teardown.
Document whether `azd down` removes the externally created group or whether
explicit group deletion is required; avoid orphaned resources.

Keep workloads and token budgets small and configurable. Do not claim a price
or cost ceiling without current pricing evidence. Developer SKU does not by
itself guarantee a low-cost lab.

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
`feature/006-controlled-failure-recovery` with code-only authorization; the full
lab has not been deployed or live-verified. Tasks `007`-`008` remain pending.

## Execution and progress

Use [TASKS.md](TASKS.md) as the authoritative task order and acceptance checklist.
Use [PROGRESS.md](PROGRESS.md) as the current handoff and append-only execution
log. Task `000` is the first implementation task; planning preparation does not
count as completing bootstrap.
