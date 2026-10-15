# APIM Responses API affinity lab

A planned lab demonstrating conversation-aware routing through Azure API
Management to two Microsoft Foundry model backends. Independent conversations
will chain Responses API requests with `previous_response_id` while retaining
backend affinity; separate scenarios will demonstrate stateless distribution,
explicit pinned-session failure, and recovery.

**Status:** Repository bootstrap implemented (task `000`); see the
[progress tracker](docs/PROGRESS.md) for PR review and merge status.
The design is not yet validated.
There is no deployable infrastructure, APIM policy, .NET harness, deployment
command, or live Azure evidence in this repository. No Azure resources have
been created by this work.

## Plan and execution

- [Lab plan and mandatory design gates](docs/PLAN.md)
- [Ordered tasks and acceptance criteria](docs/TASKS.md)
- [Current progress, authorization, and execution history](docs/PROGRESS.md)
- [Contributor and Copilot workflow](.github/copilot-instructions.md)

The intended solution uses Bicep, APIM policy XML, a .NET 10 console harness,
`azd` orchestration, an equivalent Azure CLI path, and Log Analytics evidence.
Exact platform capabilities, authentication, model configuration, and diagnostic
contracts must be verified in task `001` before solution implementation.

## Contributing

Use a focused work branch, not `main`. Keep the task and progress records
current, perform relevant checks, and open a PR when branch work is complete.
Leave PRs unmerged for the user. Start dependent work only from a merged
dependency or an explicitly documented dependency branch.

For documentation/bootstrap changes, run `git diff --check`, verify relative
links and relevant ignore behavior, and inspect the diff for scope and secrets.
Build/test commands and solution CI will be added when actual projects exist.
Text formatting is defined by `.editorconfig` and `.gitattributes`.

Never commit credentials or private environment settings. `.env.example` and
`.env.sample` files may contain placeholders only. Generated output belongs in
ignored `artifacts/` or `evidence/`; intentionally sanitized examples may be
versioned under `evidence/samples/`. Azure/azd local state is not versioned.

Writing code does not authorize Azure deployment, resource changes, controlled
faults, or teardown. Obtain explicit authorization for those operations.
