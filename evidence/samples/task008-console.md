# Task 008 - actual redacted console evidence

**Actual run, October 16, 2026; not illustrative output.** The JSON below is a
pretty-printed subset of the emitted turn fields. Session, response and gateway
request identifiers use consistent aliases; cookie fingerprints will use aliases
too. Keys, cookie values, hosts, resource identifiers and operator details are
omitted. Assistant text is actual model output, not proof of routing accuracy.

## Affinity off: a real response chain breaks across accounts

Turn 1 created a stored response on A. Turn 2 sent that actual response ID
without a cookie, reached B, and received the exact different-resource error.
The null service code is preserved: `crossResourceStateError` is the separate
strict payload classification, not a manufactured backend error code.

```json
{
  "scenario": "stateful",
  "sessionId": "<off-session-1>",
  "turn": 1,
  "prompt": "I am a developer using APIM in front of two identical Foundry Responses deployments. What must stay together when I use previous_response_id, and where does the conversation state live?",
  "previousResponseId": null,
  "responseId": "<off-response-1>",
  "backendId": "backend-a",
  "gatewayRequestId": "<off-request-1>",
  "affinityFingerprint": null,
  "httpStatus": 200,
  "errorCode": null,
  "crossResourceStateError": false,
  "outcome": "success",
  "outputText": "With `previous_response_id`, the follow-up request must reach the **same Foundry Responses deployment/backend** that created that response ID. Don\u2019t let APIM route that conversation to the other identical deployment.\n\nThe conversation state lives **in the Foundry Responses service**, associated with that backend\u2019s stored conversation/session context. **APIM is stateless** here: it forwards requests and does not store model history.\n\nIf you need continuity, keep **request + previous_response_id + backend affinity** together. Native cookie affinity can help keep a client pinned to the same backend, but the state itself is not in APIM."
}
```

```json
{
  "scenario": "stateful",
  "sessionId": "<off-session-1>",
  "turn": 2,
  "prompt": "For that same design, explain how a session-local native affinity cookie differs from my application session UUID. How should independent stateless calls behave?",
  "previousResponseId": "<off-response-1>",
  "responseId": null,
  "backendId": "backend-b",
  "gatewayRequestId": "<off-request-2>",
  "affinityFingerprint": null,
  "httpStatus": 400,
  "errorCode": null,
  "crossResourceStateError": true,
  "outcome": "httpError",
  "outputText": null
}
```

### Actual final off-mode session summary

All four session UUIDs below have been replaced with aliases. Counts, outcomes
and backend sets are unchanged from the client table.

| Scenario | Session | Outcome | Planned | Sent | Completed | Failed | Unattempted | Affinity violations | Backends |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| stateful | off-session-1 | expected-cross-backend-failure | 3 | 2 | 1 | 1 | 1 | 0 | backend-a,backend-b |
| stateful | off-session-2 | expected-cross-backend-failure | 3 | 2 | 1 | 1 | 1 | 0 | backend-a,backend-b |
| stateful | off-session-3 | expected-cross-backend-failure | 3 | 2 | 1 | 1 | 1 | 0 | backend-a,backend-b |
| stateful | off-session-4 | expected-cross-backend-failure | 3 | 2 | 1 | 1 | 1 | 0 | backend-a,backend-b |

```text
stateful: successful requests backend-a=4 backend-b=0; failed turns=4; incomplete sessions=4; distribution=observed-one (no 50/50 assertion)
Affinity OFF: PASS; cross-backend response-state failures=4; exact different-resource errors=4; cancelled/deadline=False. Same-backend success is not proof.
```

All eight request UUIDs joined to the owned workspace with actual policy
traces, matching session/member/status and concrete backend hosts within the
ten-minute ingestion budget. A generic HTTP 400 alone would not pass.

## Affinity on: the same session stays pinned

These are all three actual turns of the first healthy session. Other sessions
were interleaved between them, which explains request aliases 1, 5 and 9.
The response-ID chain advances, the same session-local pin fingerprint persists,
and all three concrete backend observations remain A.

```json
{
  "scenario": "stateful",
  "sessionId": "<on-session-1>",
  "turn": 1,
  "prompt": "I am a developer using APIM in front of two identical Foundry Responses deployments. What must stay together when I use previous_response_id, and where does the conversation state live?",
  "previousResponseId": null,
  "responseId": "<on-response-1>",
  "backendId": "backend-a",
  "gatewayRequestId": "<on-request-1>",
  "affinityFingerprint": "<on-pin-1>",
  "httpStatus": 200,
  "errorCode": null,
  "crossResourceStateError": false,
  "outcome": "success",
  "outputText": "With `previous_response_id`, the follow-up request must stay with the **same Foundry Responses deployment that created that response chain**. Don\u2019t send that continuation to a sibling deployment unless it has the same service-side state, which it usually doesn\u2019t.\n\nThe conversation state lives **in the model service/backend for that deployment**, not in APIM. APIM is **stateless**: it forwards requests and can preserve **session affinity** with native cookies, but it does **not** store model history.\n\nSo for your two identical deployments, keep each conversation pinned to one backend deployment, either by routing logic or APIM cookie affinity."
}
```

```json
{
  "scenario": "stateful",
  "sessionId": "<on-session-1>",
  "turn": 2,
  "prompt": "For that same design, explain how a session-local native affinity cookie differs from my application session UUID. How should independent stateless calls behave?",
  "previousResponseId": "<on-response-1>",
  "responseId": "<on-response-2>",
  "backendId": "backend-a",
  "gatewayRequestId": "<on-request-5>",
  "affinityFingerprint": "<on-pin-1>",
  "httpStatus": 200,
  "errorCode": null,
  "crossResourceStateError": false,
  "outcome": "success",
  "outputText": "A **session-local native affinity cookie** is emitted/used by the service or gateway to keep a client pinned to the **same backend instance/deployment**. It\u2019s a routing hint, often temporary, and it should be treated as opaque.\n\nYour **application session UUID** is your own business identifier. It represents a conversation or user session in your app, but APIM/F\u200b\u200boundry don\u2019t understand it unless you map it yourself.\n\nFor **independent stateless calls**, each request should be fully self-contained: no reliance on prior turns, no `previous_response_id`, and no expectation of affinity. They can go to either deployment because there\u2019s no state to preserve."
}
```

```json
{
  "scenario": "stateful",
  "sessionId": "<on-session-1>",
  "turn": 3,
  "prompt": "Continue our design review: what should happen when my pinned backend is unavailable, and which gateway evidence would prove continuity or an explicit failure without silently replaying history?",
  "previousResponseId": "<on-response-2>",
  "responseId": "<on-response-3>",
  "backendId": "backend-a",
  "gatewayRequestId": "<on-request-9>",
  "affinityFingerprint": "<on-pin-1>",
  "httpStatus": 200,
  "errorCode": null,
  "crossResourceStateError": false,
  "outcome": "success",
  "outputText": "If the pinned backend is unavailable, the request should **fail fast** for any stateful chain using `previous_response_id`, rather than silently switching to the other deployment. A failover to a different backend would break continuity because the state lives with the original backend.\n\nGateway evidence to prove this:\n- The **same affinity cookie / backend pin** was present on prior successful calls.\n- The response shows the **same backend identity** was used across turns, if you log it.\n- On failure, APIM returns a clear **upstream 5xx/timeout** with no retry that changes backend.\n- If continuity is lost, you should see an **explicit routing change or cache miss**, not a replay of old history."
}
```

The model's failure description is not a service guarantee. Actual breaker
semantics and their bounded proof are in the
[gateway contract](../../infra/policies/README.md); task `008` did not inject
a new outage.

### Actual final healthy session summary

Only session identifiers have changed to aliases. The complete client table
retains all four stateful sessions and all twelve independent stateless calls.

| Scenario | Session | Outcome | Planned | Sent | Completed | Failed | Unattempted | Affinity violations | Backends |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| stateful | on-session-1 | complete | 3 | 3 | 3 | 0 | 0 | 0 | backend-a |
| stateful | on-session-2 | complete | 3 | 3 | 3 | 0 | 0 | 0 | backend-b |
| stateful | on-session-3 | complete | 3 | 3 | 3 | 0 | 0 | 0 | backend-a |
| stateful | on-session-4 | complete | 3 | 3 | 3 | 0 | 0 | 0 | backend-b |
| stateless | on-stateless-1 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-a |
| stateless | on-stateless-2 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-b |
| stateless | on-stateless-3 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-a |
| stateless | on-stateless-4 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-b |
| stateless | on-stateless-5 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-a |
| stateless | on-stateless-6 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-b |
| stateless | on-stateless-7 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-a |
| stateless | on-stateless-8 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-b |
| stateless | on-stateless-9 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-a |
| stateless | on-stateless-10 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-b |
| stateless | on-stateless-11 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-a |
| stateless | on-stateless-12 | complete | 1 | 1 | 1 | 0 | 0 | 0 | backend-b |

```text
stateful: successful requests backend-a=6 backend-b=6; failed turns=0; incomplete sessions=0; distribution=observed-both (no 50/50 assertion)
stateless: successful requests backend-a=6 backend-b=6; failed turns=0; incomplete sessions=0; distribution=observed-both (no 50/50 assertion)
Run: PASS; cancelled/deadline=False; expected-failure scenarios=0 (healthy run).
```
