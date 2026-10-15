import test from 'node:test';
import assert from 'node:assert/strict';
import { acceptTurn, affinityCookie, nextAttempt, responseBody, responseHeaders } from './probe.mjs';

function headers(backend = 'backend-a', cookie = 'pin-a') {
  const values = new Headers({ 'x-lab-backend-id': backend, 'x-probe-request-id': 'gateway-request' });
  if (cookie) values.append('set-cookie', `APIM-Backend-Affinity=${cookie}; Secure; HttpOnly; Path=/`);
  return values;
}

test('failed calls consume budget; the 65th attempt is rejected', () => {
  const ledger = Array.from({ length: 63 }, (_, i) => JSON.stringify({ number: i + 1 })).join('\n');
  assert.equal(nextAttempt(ledger), 64);
  assert.throws(() => nextAttempt(`${ledger}\n{"number":64}\n`), /budget exhausted/);
  assert.throws(() => nextAttempt('invalid ledger'), SyntaxError);
});

test('sessions isolate cookie and response chains and preserve cookies without a new header', () => {
  const a = { cookie: '', previous: '', backend: '', turns: 0 };
  const b = { cookie: '', previous: '', backend: '', turns: 0 };
  acceptTurn(a, 200, { id: 'response-a', status: 'completed' }, headers());
  acceptTurn(b, 200, { id: 'response-b', status: 'completed' }, headers('backend-b', 'pin-b'));
  acceptTurn(a, 200, { id: 'response-a2', status: 'completed' }, headers('backend-a', ''));
  assert.equal(a.cookie, 'APIM-Backend-Affinity=pin-a');
  assert.equal(b.cookie, 'APIM-Backend-Affinity=pin-b');
  assert.equal(a.previous, 'response-a2');
  assert.equal(b.previous, 'response-b');
  assert.equal(a.turns, 2);
});

test('backend switches and incomplete evidence cannot advance a chain', () => {
  const a = { cookie: 'APIM-Backend-Affinity=pin-a', previous: 'response-a', backend: 'backend-a', turns: 1 };
  const before = { ...a };
  assert.throws(() => acceptTurn(a, 200, { id: 'other', status: 'completed' }, headers('backend-b')), /changed backend/);
  assert.throws(() => acceptTurn(a, 200, { id: 'other', status: 'completed' }, headers('unresolved')), /evidence missing/);
  assert.throws(() => acceptTurn(a, 200, { id: 'other', status: 'incomplete' }, headers()), /incomplete/);
  assert.deepEqual(a, before);
  const failed = acceptTurn(a, 404, { error: { code: 'not_found' } }, headers('backend-b'));
  assert.equal(failed.advanced, false);
  assert.deepEqual(a, before);
});

test('empty and duplicate affinity cookies fail explicitly', () => {
  const empty = new Headers({ 'set-cookie': 'APIM-Backend-Affinity=; Path=/' });
  assert.throws(() => affinityCookie(empty), /Empty affinity cookie/);
  const duplicate = headers();
  duplicate.append('set-cookie', 'APIM-Backend-Affinity=pin-b; Path=/');
  assert.throws(() => affinityCookie(duplicate), /Ambiguous affinity cookies/);
  assert.equal(affinityCookie(new Headers()), '');
});

test('native transport preserves separate Set-Cookie values for affinity extraction', () => {
  const headers = responseHeaders([
    'Set-Cookie', 'other=value; Path=/',
    'Set-Cookie', 'APIM-Backend-Affinity=pin-a; Path=/',
    'X-Probe-Request-Id', 'request',
  ]);
  assert.equal(affinityCookie(headers), 'APIM-Backend-Affinity=pin-a');
  assert.equal(headers.get('x-probe-request-id'), 'request');
});

test('non-JSON gateway errors remain observable failures without advancing a chain', () => {
  const session = { cookie: 'APIM-Backend-Affinity=pin-a', previous: 'response-a', backend: 'backend-a', turns: 1 };
  const parsed = responseBody(503, '<html>Service Unavailable</html>');
  const accepted = acceptTurn(session, 503, parsed.body, headers(''));
  assert.equal(parsed.bodyFormat, 'non-json-error');
  assert.equal(accepted.advanced, false);
  assert.equal(session.previous, 'response-a');
  assert.throws(() => responseBody(200, '<html>not a model response</html>'), /Non-JSON successful/);
});
