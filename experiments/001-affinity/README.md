# Task 001 disposable affinity experiment

This is a **feasibility probe**, not the self-service lab or tasks `002`-`008`.
It uses real billable resources and a reversible backend-path fault. Its initial
execution was explicitly authorized on 2026-10-15; this is not blanket approval
to run it in another subscription or against existing resources.

## Scope and safeguards

The Bicep deploys Developer APIM (one unit), two identical GPT-5.4 mini
`2026-03-17` / `GlobalStandard` deployments (capacity 20 each), and a workspace
in West US 3. Resource-group creation is outside Bicep. Global inference is not
a West US 3-only processing guarantee.

The Node probe uses built-in modules only, native affinity cookies, independent
response-ID chains, API-scoped APIM access, and gateway managed identity.
Credentials, cookies, response objects, and raw debug transcripts remain in
memory. Generated metadata and deployment configuration belong in ignored
`artifacts/`, not this directory.

- The shared attempt ledger rejects a 65th request, including across reruns.
  Do not remove it to reset a run's budget.
- Model requests have short prompts, 2,048 maximum output tokens, no automatic
  transport retries, and a 60-second timeout. Starts are normally ten seconds
  apart; the three fresh-session checks and original-cookie check use two
  seconds so they fit inside the configured 30-second breaker-open window.
  A single keep-alive HTTPS connection avoids repeated TLS handshakes; it does
  not share cookies between sessions or disable certificate verification.
- Stateful sessions retain only their own cookie and latest successful ID.
  Missing member/correlation evidence, incomplete output, or backend changes
  fail explicitly.
- A separate eight-call `store=false` sample never replays a cookie or response
  ID. Small samples do not promise an exact distribution.
- Fault injection changes only the selected owned backend's path, never its
  host, authentication, or model deployment. Restoration runs in `finally`;
  concurrent URL changes block a blind restore.
- Gateway logging has 100% sampling, information-level policy traces, no body
  or header capture, no client IP, 30-day retention, and a 1 GB/day workspace
  quota. The quota can overshoot; it is not a billing cap.
- `Watch-Cleanup.ps1` waits for an exact local signal or deadline, checks group
  ownership, cancels active deployments, deletes that group, and verifies its
  absence. It must stay attached/running until cleanup completes. It is not
  a persistent cloud cleanup service and cannot survive terminal shutdown.
  Group deletion can leave recovery records. In this run, the two deleted
  Foundry accounts still held 40 quota units until their exact ownership-tagged
  records were purged; cleanup then returned quota to its original baseline.
  APIM/workspace normal recovery records were retained and explicitly reported.

The experiment intentionally tests the native pool, not a custom pin token,
response-ID cache, or replay workaround. An HTTP failure after selection of
another backend is recorded separately from strict original-member preservation.
Neither a 2xx nor an arbitrary 4xx alone closes the design gate.
Native breaker rejection can have a non-JSON 503 body and no concrete-member
response header. Preserve that error and inspect its debug/workspace evidence;
do not invent a backend header when forwarding never happened.

## Local checks

```powershell
az bicep build --file .\experiments\001-affinity\main.bicep --outfile .\artifacts\001-template.json
node --check .\experiments\001-affinity\probe.mjs
node --test .\experiments\001-affinity\probe.test.mjs
```

Do not copy private deployment parameters into tracked files. Live execution
requires exact-target authorization, price/quota checks, ARM validation and
what-if, a verified running cleanup watcher, and an inspected dedicated group.
See [progress](../../docs/PROGRESS.md) for actual execution status and
[design](../../docs/DESIGN.md) for unresolved versus verified behavior.

The probe config has `subscription`, `resourceGroup`, `prefix`, `session`, and
`expiresUtc`. The group must carry matching `session` and
`purpose=task001-disposable-feasibility` tags. Use ignored files for the config.
After deployment succeeds, the probe and workspace collector entry points are:

```powershell
node .\experiments\001-affinity\probe.mjs .\artifacts\001-config.json
node .\experiments\001-affinity\collect-logs.mjs .\artifacts\001-config.json <result-file> <workspace-customer-id>
```

`--focus-failure` skips the already-tested healthy chains and stateless sample;
its result marks those checks as not run, not passed. It is useful only with
separate retained evidence for them. All modes share the same 64-attempt ledger.

Result files contain sanitized routing/status/usage metadata, cookie/ID
fingerprints rather than raw session state, and allow-listed debug backend URLs.
The collector requires correlated gateway rows and nonempty `affinity-probe`
policy trace content; it does not equate access logs alone with tracing.
The complete lab will use the separately planned .NET 10 harness and `azd`;
these experiment scripts are not that delivery.
