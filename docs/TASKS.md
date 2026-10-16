# Task execution order

Task `000` is complete and PR #2 is merged. Task `001` completed the authorized
disposable feasibility test and cleanup; B1/B2 are resolved in
[DESIGN.md](DESIGN.md), with evidence in merged PR #3. Task `002` is complete
as code and local validation in merged PR #4. Task `003`
is complete as code and local validation in merged PR #5. Task `004` is complete
as offline-validated code in merged PR #6. Task `005` is complete as code and
offline validation in merged PR #7. Task `006` is complete as code and offline
validation in merged PR #8. Task `007` completed both authorized live
lifecycles and cleanup on 2026-10-15/16 in merged PR #9;
task `008` delivery and its README/code-citation follow-up are complete in
PR #10, left unmerged for user review on the same dependency branch.
No new Azure operations are authorized by the documentation follow-up.
IDs are stable; do not renumber completed tasks or hide blocked work.

## Status and dependency rules

Statuses: `pending`, `in_progress`, `blocked`, `done`.

Before starting, mark the task `in_progress` and record its branch in
[PROGRESS.md](PROGRESS.md). A task is `done` only when its acceptance criteria
are satisfied, evidence is recorded, and its branch work has a pull request.
An open PR is not a merged dependency: dependent work must use the documented
dependency branch or wait for the user to merge it. Never merge automatically.

Update this table and the progress log together. Mark a blocker with a concrete
reason and the condition needed to resume. Correct the plan explicitly when
new platform evidence changes an assumption.

## Ordered backlog

| ID | Task | Depends on | Status |
| --- | --- | --- | --- |
| 000 | Bootstrap repository and contributor workflow | Implementation authorization; base branch established | done |
| 001 | Validate platform capabilities and finalize design | 000 | done |
| 002 | Establish Bicep infrastructure and observability foundation | 001 | done |
| 003 | Implement gateway routing, affinity, and diagnostic policies | 002 | done |
| 004 | Implement deployment orchestration and Azure CLI parity | 003 | done |
| 005 | Implement .NET 10 conversation harness and evidence model | 003 | done |
| 006 | Implement controlled failure, breaker, and recovery scenarios | 004, 005 | done |
| 007 | Verify end-to-end scenarios and diagnostic evidence in Azure | 006 | done |
| 008 | Complete self-service documentation, affinity toggle, and clean-environment walkthrough | 007 | done |

The default execution order is ascending ID. Tasks `004` and `005` can proceed
independently once `003` is available; parallel execution is not required.

## 000 - Bootstrap repository and contributor workflow

**Scope:** Repository hygiene and execution conventions only. No Bicep resources,
APIM policy implementation, harness application, deployment scripts, or Azure
resource creation.

Deliverables and acceptance:

- Preserve the approved initial base: `main` was established at empty commit
  `846a2cb` on 2026-10-15. This prerequisite is resolved; no implementation
  commits may be made directly on `main`.
- Create `.github\copilot-instructions.md` with the workflow from
  [PLAN.md](PLAN.md): no direct work on `main`; use feature/chore/bugfix/etc.
  branches; create a PR when branch work is complete; never merge it.
- Include planning/progress maintenance, scope boundaries, required checks,
  safe credential handling, and the rule that deployment needs authorization.
- Create a repository-appropriate `.gitignore` for anticipated .NET build
  outputs, IDE-local settings, Azure/azd local state, secrets, and generated
  evidence. Preserve intentionally versioned templates and sample configuration.
- Add a minimal root README that identifies the planned lab, links these
  documents, and clearly states the implementation status.
- Add lightweight text-formatting conventions such as `.editorconfig` and
  `.gitattributes` where useful; avoid scaffolding unused tooling.
- Establish a focused PR template with scope, validation, deployment impact,
  and the linked task ID. Do not add solution CI before validation commands
  and project files actually exist.
- Record the branch, changed files, and bootstrap PR in the progress tracker.

## 001 - Validate platform capabilities and finalize design

**Scope:** Evidence-backed design plus the explicitly authorized disposable
feasibility deployment, fault test, evidence collection, and verified cleanup.
Use Developer APIM (one unit), two small identical GPT-5.4 mini deployments,
and bounded diagnostics in a new dedicated resource group. This is not task
`002` infrastructure or authorization for tasks `002`-`008`.

**Result:** B1/B2 resolved with real inference, debug/workspace correlation,
explicit original-pin breaker rejection, healthy fresh sessions, and recovery.
The exact owned group was deleted; both Foundry recovery records were purged
to release model quota. APIM/workspace recovery records retain their normal
Azure soft-delete periods. Selected native policy traces were ingested and
correlated. See [DESIGN.md](DESIGN.md) and
[sanitized evidence](../evidence/samples/task001-feasibility.json). PR #3 merged
on 2026-10-15; its feasibility approval did not authorize later tasks. Task `002`
has separate code-only authorization below.

Deliverables and acceptance:

- Resolve every mandatory design gate in `PLAN.md`.
- Record official source links, verification dates, selected resource/API
  versions, model/deployment configuration, auth design, and known limitations.
- Use read-only Azure checks for the current subscription's availability and
  quota when authorized; do not switch subscription or region silently.
- Specify the affinity token lifecycle, response-ID chain, backend identity,
  diagnostic correlation contract, and new-session versus pinned-session paths.
- Specify observable breaker behavior, fault injection/restoration, expected
  failure errors, and the convergence window used by the scenarios.
- Document resource diagnostic capabilities, trace destinations, log categories,
  retention intent, and any additional required observability resources.
- Define configuration names and intended deployment outputs shared by Bicep,
  orchestration, and the harness. Keep secrets out of ordinary outputs.
- Stop and record blockers if the requested SKU, region, or failure/tracing
  semantics cannot be supported. Obtain decisions before changing requirements.

## 002 - Establish Bicep infrastructure and observability foundation

**Authorization:** On 2026-10-15, after merging PR #3, the user requested the
next task. Work on `feature/002-infrastructure-foundation` covers infrastructure
code, local checks, documentation, and a PR only; no live Azure operations.

**Result:** Implemented in `infra/` and published in PR #4, merged on 2026-10-15.
Bicep build/lint, native `.bicepparam` compilation, and six offline contract
checks passed. All inventoried parent diagnostic categories are configured.
Provider-side validation and runtime emission are explicitly unverified and
remain deployment/task `007` gates, not inferred successes.

Deliverables and acceptance:

- Implement resource-group-scoped Bicep for Developer APIM, two Foundries with
  identical model deployments, Log Analytics, and design-approved dependencies.
- Keep resource-group creation outside the Bicep template.
- Parameterize names, the verified model/version, and safe lab-scale capacity;
  default all regional resources to West US 3.
- Enable public network access while preserving required authentication.
- Implement selected identities and least-privilege role assignments.
- Enable all supported resource diagnostic categories toward the workspace;
  keep the unsupported-resource inventory explicit.
- Expose the agreed non-secret endpoints/identifiers needed by later tasks.
- Run Bicep build/lint and appropriate non-destructive template validation.
  Record anything requiring an actual deployment for task `007`.

## 003 - Implement gateway routing, affinity, and diagnostic policies

**Authorization:** On 2026-10-15, after merging PR #4, the user requested the
next work. `feature/003-gateway-routing-policies` covers code, local checks,
documentation, and PR publication only. No new live Azure operations.

**Result:** Implemented in PR #5, merged on 2026-10-15. Bicep build/
lint and native parameter compilation passed; 11 compiled contract tests,
all 18 C# 7 policy expressions, 39 input/host fixtures, and XML/auth/forward/
trace checks passed locally. New policy runtime, cookie expiry/binding limits,
native failure behavior, and diagnostic ingestion/privacy remain explicitly
documented task `005`/`007` responsibilities, not inferred live successes.

Deliverables and acceptance:

- Define both backends, circuit breakers, and the load-balancing pool using the
  supported configuration established in `001`.
- Implement the Responses proxy and affinity contract in Bicep/policy XML.
- Handle first turns, subsequent turns, and independent stateless requests.
- Preserve required request/response behavior and return explicit errors when
  a pinned conversation cannot continue on its intended backend.
- Expose safe, gateway-derived backend identity and request correlation to the
  harness. Document any lab-only headers or policies.
- Configure the approved trace/diagnostic path into Log Analytics; do not
  substitute a console header for the requested workspace evidence.
- Validate XML, Bicep, and the relevant policy contracts locally where possible.
  Track runtime-only assertions for task `007`.

## 004 - Implement deployment orchestration and Azure CLI parity

**Authorization:** On 2026-10-15, after merging PR #5, the user requested the
next task. `feature/004-deployment-orchestration` covers code, offline checks,
documentation, and PR publication only. No live Azure checks, deployment,
resource changes, inference, faults, teardown, or credential retrieval.

**Result:** Both lifecycle drivers and four azd hooks are implemented in PR #6,
merged on 2026-10-15.
Bicep/native parameter compilation, 11 foundation/gateway contracts, all 18
policy expressions/39 fixtures, 28 offline lifecycle cases, PowerShell syntax,
and YAML hook/configuration checks passed. No live Azure checks were run;
actual provisioning/reruns, credential handoff, and teardown remain task `007`
gates, not inferred successes from offline mocks.

Deliverables and acceptance:

- Add `azd` configuration and hooks/scripts that create or select the dedicated
  lab resource group outside Bicep and use the agreed infrastructure templates.
- Add the Azure CLI-only path using those same templates and parameter contract.
- Check required tools, Azure sign-in, active subscription, region, model
  availability/quota, and required permissions before deployment.
- Display the target subscription and group; fail clearly rather than falling
  back to another target.
- Hand non-secret deployment configuration to the harness without manual
  endpoint guessing; handle credentials through the approved mechanism.
- Make reruns predictable and document ownership of environment-local files.
- Implement scoped teardown and explain group ownership for both paths.
- Validate configuration and script syntax; record deployed-path checks for `007`.

## 005 - Implement .NET 10 conversation harness and evidence model

**Authorization:** On 2026-10-15, after merging PR #6, the user requested the
next task. `feature/005-conversation-harness` covers harness code, offline
checks, documentation, and PR publication only. No Azure checks, real inference,
credential retrieval, resource changes, controlled faults, or teardown.

**Local result:** Implemented the .NET 10 console client, session/evidence model,
interleaved stateful and independent stateless scenarios, strict bounds and
failure summaries. Release build passed with no warnings/errors; 59 focused
transport/configuration tests, formatting verification, publish, offline CLI
guards, and 28 lifecycle regression cases passed. Real inference, model answer
quality, native routing, and trace ingestion remain task `007` assertions.
Published in PR #7, merged on 2026-10-15.

Deliverables and acceptance:

- Create a .NET 10 console harness with a small scripted developer/architect
  conversation about Responses affinity through APIM.
- Isolate each session's response IDs and affinity transport. Use
  `previous_response_id` for subsequent turns.
- Support a multi-session affinity scenario and a separate stateless
  distribution scenario.
- Print model responses and the per-turn evidence contract from `PLAN.md`.
- Summarize each session's backend set, turns, failures, and affinity violations;
  summarize aggregate distribution and clearly separate scenario results.
- Produce a nonzero exit code for unexpected errors or violated assertions;
  never manufacture successful evidence from missing headers or logging.
- Bound token use, session/turn counts, timeouts, and retries. Do not retry a
  pinned conversation by silently switching backends or replaying its history.
- Add focused tests for session isolation, response chaining, affinity
  violations, missing evidence, and summary/failure accounting.
- Build and run focused tests without requiring live Azure for those tests.

## 006 - Implement controlled failure, breaker, and recovery scenarios

**Authorization:** On 2026-10-15, after merging PR #7, the user requested the
next task. `feature/006-controlled-failure-recovery` covers code, offline
checks, documentation, and PR publication only. No live Azure reads, credential
retrieval, inference, backend changes, controlled faults, or teardown.

**Local result:** Implemented the separately gated failure workflow, exact
resource/ownership/ETag checks, journal-before-write URL-only fault/restoration,
conservative breaker-window assertions, and original-chain recovery. Release
build, 113 offline .NET cases, formatting, publish, 30 lifecycle cases, Bicep
compilation, 11 foundation/gateway contracts, and 19 compiled policy expressions
with 42 cases passed. Published CLI guards, 24-file text/scope checks, 67
relative links/anchors, and nine ignore fixtures passed. Published in PR #8,
merged on 2026-10-15.
No live Azure operation was performed; task `007` must verify this exact path.

Deliverables and acceptance:

- Add the reversible fault procedure chosen in `001`, scoped to this lab only.
- Capture a known backend-bound session before injecting the failure.
- Show that session failing explicitly and fresh sessions reaching the healthy
  backend after breaker detection.
- Capture observed timing and errors, rather than asserting immediate failover.
- Restore the backend even when the scenario exits with an error, and expose
  restoration failures clearly.
- Demonstrate recovery and retain correlated evidence for the summary.
- Keep this destructive-to-the-lab scenario opt-in and clearly documented.

## 007 - Verify end-to-end scenarios and diagnostic evidence in Azure

**Authorization:** On 2026-10-15, after merging PR #8, the user approved starting
live verification on `feature/007-live-verification`. Read-only preparation is
authorized against the active subscription and agreed West US 3 target.
Exact new-resource targets and provisioning/fault/cleanup actions are confirmed
before execution; no existing application resources may be adopted or changed.

**Execution authorization:** Earlier unavailable confirmation attempts were not
approval. After the exact scoped handoff, the user explicitly instructed
autonomous continuation at 16:31 CDT on 2026-10-15. Proceed with the new
`rg-responses-verify007-azd` group, then `rg-responses-verify007-cli`, in the
unchanged active subscription / West US 3. This includes bounded inference,
reversible fault/restoration, unchanged reruns, and exact owned-group deletion.
No existing application resources may be adopted. No deployment or scenario
success is inferred from this authorization.
The user's subsequent APIM user-assigned-identity directive authorizes one
new dedicated identity in each owned group, not an existing identity or any
broader role grant. Validate its attachment, policy client ID, role scopes,
ownership checks, and teardown with the rest of the lab.

**Preparation result (historical):** Fixed two actual CLI contract defects: nullable model
catalog minimum/step and default plain-text `azd auth token` output. All 33
offline lifecycle cases and the real read-only model/provider/capacity preflight
passed. The supported isolated azd-to-CLI delegated identity was verified in
memory. Preparation fixes were initially published in draft PR #9, not then a
completed task `007` delivery. Provider validation/what-if subsequently ran
against each authorized owned group; preparation results alone are not
deployment or scenario evidence.

**Live result:** Guarded azd and CLI provisioning, dedicated user-assigned
identity/policy/two-grant checks, 24 healthy calls, an eight-attempt conditional
fault/restoration/recovery run, and all 32 correlated native policy traces
passed in each environment. Live category/privacy/retention checks and both
unchanged reruns passed. Initial CLI provisioning polling failed after ARM
success; fresh state checks and guarded export reconciled it. Azd deletion
required handling APIM's transition refusal and a later credential-poll error.
Both groups are verified absent and state retired. Per group, one APIM, two
Foundry, and one workspace recovery record remain without purge; final exact
model quota was 130 of 1,000 versus 50 initially.
Task `007` is complete in merged PR #9; task `008` is in progress. See
[sanitized evidence](../evidence/samples/task007-verification.json).

Deliverables and acceptance:

- Provision a clean lab through `azd`; record the actual subscription, region,
  SKU, model configuration, and resource-group ownership without secrets.
- Run stateful sessions and confirm zero unintended backend switches.
- Observe both healthy backends across independent sessions and separately
  demonstrate stateless distribution. Report actual counts, not ideal ratios.
- Run the failure scenario and prove pinned failure, healthy new sessions, and
  backend restoration/recovery with correlated evidence.
- Query Log Analytics for supported resource diagnostics and the agreed APIM
  trace records. Account for ingestion delay and record missing evidence as a
  failure, not success.
- Verify the Azure CLI-only path on a clean environment as well, reusing
  templates rather than maintaining a second infrastructure implementation.
- Exercise rerun behavior and teardown for both paths; record residual resources
  or retention behavior explicitly.
- Record commands, redacted evidence, outcomes, and any limitations. Fix
  task-caused defects before marking this task done.

## 008 - Complete self-service documentation and walkthrough

**Authorization:** On 2026-10-16, after PR #9 merged, the user authorized task
`008` on `feature/008-self-service-affinity-toggle`. Cost documentation is
explicitly excluded. The added enhancement is a simple backend-pool session
affinity parameter, enabled by default, with an off/on response-chain
comparison. After the exact target/action handoff, the user approved a new
environment in West US 3 in the same subscription: `walk008` /
`rg-responses-walk008`, bounded off/on inference, affinity-only redeployment,
driver parity/reruns, verification and scoped deletion without purge.
The ceiling is 64 attempts at 2,048 output tokens each, without retries.
On the same date, the user requested a documentation-only follow-up in the
README explaining the native affinity setting, per-conversation cookie
capture/resend, and response-ID chain with code excerpts and source-line
citations. Continue on the existing PR #10 dependency branch; no new Azure
operation or application change is included.

**Verified result:** Native off/on configuration, four genuine cross-resource
state failures, 24 successful affinity-on requests, all 42 trace joins including
diagnostics, unchanged 13-value rerun, and scoped cleanup/retired state passed.
Actual redacted turn output and complete final tables are in the
[console sample](../evidence/samples/task008-console.md). Published in PR #10
on 2026-10-16, left open and unmerged for user review.

Deliverables and acceptance:

- Complete the root README with prerequisites, architecture, state ownership,
  affinity lifecycle, auth, policies, breakers, and limitations.
- Tie the native affinity setting to actual harness cookie capture/resend and
  `previous_response_id` handling using accurate code excerpts and source-line
  citations; explain session isolation, stateless calls, and failure guards.
- Document both complete deployment paths, harness commands and scenario
  expectations, Log Analytics queries, troubleshooting, and scoped teardown.
- Include example redacted per-turn output and final summary from a real
  verified run; label illustrative samples if any are not actual run evidence.
- Explain token/capacity controls and cleanup responsibilities; cost
  documentation is excluded by the user's task `008` scope change.
- Add a deployment parameter that enables/disables native backend-pool session
  affinity, defaulting to enabled, through both deployment drivers.
- Demonstrate an unpinned cross-backend response-chain failure versus successful
  pinned chains. Do not treat missing client cookies, a policy rejection, or
  accidental same-backend routing as proof of a backend response-state failure.
- Walk through the instructions from a clean environment and fix missing steps.
- Update the plan and task/progress records to match delivered behavior,
  link completion PRs, and leave PRs unmerged for the user.
