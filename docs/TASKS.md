# Task execution order

Task `000` is complete and PR #2 is merged. Task `001` completed the authorized
disposable feasibility test and cleanup; B1/B2 are resolved in
[DESIGN.md](DESIGN.md), with evidence in PR #3. Later tasks remain pending.
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
| 002 | Establish Bicep infrastructure and observability foundation | 001 | pending |
| 003 | Implement gateway routing, affinity, and diagnostic policies | 002 | pending |
| 004 | Implement deployment orchestration and Azure CLI parity | 003 | pending |
| 005 | Implement .NET 10 conversation harness and evidence model | 003 | pending |
| 006 | Implement controlled failure, breaker, and recovery scenarios | 004, 005 | pending |
| 007 | Verify end-to-end scenarios and diagnostic evidence in Azure | 006 | pending |
| 008 | Complete self-service documentation and clean-environment walkthrough | 007 | pending |

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
[sanitized evidence](../evidence/samples/task001-feasibility.json). PR #3 remains
unmerged; tasks `002`-`008` are neither implemented nor authorized by this work.

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

**Gate:** Azure deployment and the controlled fault scenario require explicit
authorization; previous permission to write code alone does not authorize them.

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

Deliverables and acceptance:

- Complete the root README with prerequisites, architecture, state ownership,
  affinity lifecycle, auth, policies, breakers, and limitations.
- Document both complete deployment paths, harness commands and scenario
  expectations, Log Analytics queries, troubleshooting, and scoped teardown.
- Include example redacted per-turn output and final summary from a real
  verified run; label illustrative samples if any are not actual run evidence.
- Explain cost drivers, token/capacity controls, and cleanup responsibilities.
- Walk through the instructions from a clean environment and fix missing steps.
- Update the plan and task/progress records to match delivered behavior,
  link completion PRs, and leave PRs unmerged for the user.
