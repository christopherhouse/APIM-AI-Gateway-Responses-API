# Repository instructions

## Scope and authorization

- Read `docs/PLAN.md`, `docs/TASKS.md`, and `docs/PROGRESS.md` before starting.
  The task backlog defines acceptance criteria; the progress tracker records
  authorization, current state, and evidence.
- Work only on the explicitly authorized task or documented group of tasks.
  Do not scaffold later tasks or treat code-writing permission as deployment
  permission. Record blockers rather than silently changing requirements.
- Task `001` must resolve the design gates before infrastructure, APIM policies,
  deployment orchestration, or the harness are implemented. Do not assume
  unverified platform features, model availability, or failure semantics.
- Azure deployment, resource changes, controlled faults, and teardown require
  explicit authorization for the target and action. Read-only subscription
  checks also require authorization. Never silently switch subscription,
  region, SKU, or existing resource targets.

## Branch and pull request workflow

- Never implement or commit directly on `main`. Start from the latest merged
  `main` on a descriptive `feature/*`, `chore/*`, `bugfix/*`, or suitable branch.
  Preserve unrelated local changes; do not reset or overwrite them.
- Keep branch scope coherent. Before work, mark the task `in_progress` in
  `docs/TASKS.md` and record its branch and authorization in `docs/PROGRESS.md`.
- Keep the plan, task status, and progress checkpoint consistent. Append dated
  execution entries with changed files, checks and observed results, blockers,
  and the next action. Never rewrite historical entries to imply newer results.
- Run relevant checks, inspect the diff for scope and secrets, commit, push the
  work branch, and create a PR linked to the task ID. Record the PR in progress.
  A task is `done` only after its acceptance criteria and evidence are complete
  and a PR exists.
- Never merge PRs automatically. An open PR is not a merged dependency: wait
  for the user to merge it or explicitly document use of its dependency branch.

## Required checks

- For every change, run `git diff --check` and inspect the changed-file list.
- For documentation/bootstrap changes, check relative links, task/progress
  consistency, and relevant ignore rules. Follow `.editorconfig` and
  `.gitattributes`; keep committed text free of trailing whitespace.
- Once code exists, use the smallest applicable repository build, lint, and
  focused test commands. Bicep changes need build/lint; policy changes need XML
  and contract checks; harness changes need build and focused tests.
- Do not invent successful validation or substitute local checks for live Azure
  evidence. Record checks not run and why. Do not add solution CI until project
  files and working validation commands exist.

## Credentials and generated files

- Never commit credentials, access tokens, keys, connection strings, private
  environment configuration, or raw sensitive request/response traces.
- Keep local secrets in the design-approved credential mechanism, not source
  code, ordinary deployment outputs, command logs, or PR descriptions.
- Commit only placeholder values in sample configuration. Keep versioned
  templates and sanitized examples separate from generated environment state.
- Use `artifacts/` or `evidence/` for generated output; only intentionally
  sanitized examples in `evidence/samples/` are eligible for version control.
  Azure/azd local state, build outputs, and local environment files are ignored.
  Ignore rules are not a security boundary: inspect staged content before push.
