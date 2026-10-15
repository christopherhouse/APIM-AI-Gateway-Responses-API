import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { randomUUID, createHash } from 'node:crypto';
import {
  appendFileSync, closeSync, existsSync, mkdirSync, openSync, readFileSync,
  unlinkSync, writeFileSync,
} from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as sleep } from 'node:timers/promises';
import https from 'node:https';

const apiVersion = '2024-05-01';
const backendVersion = '2025-03-01-preview';
const members = ['backend-a', 'backend-b'];
const fingerprint = value => createHash('sha256').update(value).digest('hex').slice(0, 12);

export function nextAttempt(ledger) {
  const entries = ledger.trim() ? ledger.trim().split('\n').map(line => JSON.parse(line)) : [];
  assert(entries.length < 64, '64-request budget exhausted; no request sent');
  return entries.length + 1;
}

export function affinityCookie(headers, previous = '') {
  const values = headers.getSetCookie();
  const selected = values.filter(value => value.startsWith('APIM-Backend-Affinity='));
  assert(selected.length <= 1, 'Ambiguous affinity cookies');
  if (!selected.length) return previous;
  const cookie = selected[0].split(';')[0];
  assert(cookie.length > 'APIM-Backend-Affinity='.length, 'Empty affinity cookie');
  return cookie;
}

export function acceptTurn(session, status, body, headers) {
  const backend = headers.get('x-lab-backend-id');
  const requestId = headers.get('x-probe-request-id');
  assert(requestId, 'Gateway correlation missing');
  if (status < 200 || status >= 300) return { backend, requestId, advanced: false };
  assert(members.includes(backend), 'Concrete backend evidence missing');
  assert(body.id && body.status === 'completed', 'Response incomplete or missing ID');
  const cookie = affinityCookie(headers, session.cookie);
  assert(cookie, 'Native affinity cookie missing');
  assert(!session.backend || session.backend === backend, 'Chained session changed backend');
  session.backend = backend;
  session.previous = body.id;
  session.cookie = cookie;
  session.turns++;
  return { backend, requestId, advanced: true };
}

export function responseHeaders(rawHeaders) {
  const headers = new Headers();
  for (let i = 0; i < rawHeaders.length; i += 2) {
    headers.append(rawHeaders[i], rawHeaders[i + 1]);
  }
  return headers;
}

export function responseBody(status, text) {
  try { return { body: JSON.parse(text), bodyFormat: 'json' }; }
  catch {
    assert(status >= 400, `Non-JSON successful gateway response: HTTP ${status}`);
    return { body: {}, bodyFormat: 'non-json-error' };
  }
}

async function main(configPath, failureFocus) {
  const config = JSON.parse(readFileSync(configPath, 'utf8'));
  assert.match(config.subscription, /^[a-f0-9-]{36}$/);
  assert.match(config.resourceGroup, /^rg-ai-affinity-001-[a-f0-9]{8}-\d{4}$/);
  assert.match(config.prefix, /^[a-z0-9]{6,16}$/);
  assert.match(config.session, /^[a-f0-9-]{36}$/);
  const deadline = Date.parse(config.expiresUtc) - 20 * 60_000;
  assert(Number.isFinite(deadline) && Date.now() < deadline, 'Test deadline reached');
  mkdirSync('artifacts', { recursive: true });
  const lockPath = resolve('artifacts', '001-probe.lock');
  const lock = openSync(lockPath, 'wx');
  const ledgerPath = resolve('artifacts', '001-attempts.jsonl');
  const resultPath = resolve('artifacts', `001-results-${new Date().toISOString().replaceAll(':', '-')}.json`);
  const result = {
    started: new Date().toISOString(), transport: 'node-https-single-keepalive-no-retry',
    failureFocus, calls: [], findings: {}, traces: [],
  };
  let originalBackend;
  let faultPath;
  let faultUrl;
  let arm;
  let apimPath;
  const agent = new https.Agent({ keepAlive: true, maxSockets: 1, timeout: 60_000 });

  try {
    // The fixed command contains no user input or credentials.
    const account = JSON.parse(execFileSync('cmd.exe', [
      '/d', '/s', '/c', 'az account show --output json',
    ], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
    assert.equal(account.id, config.subscription, 'Active subscription changed');
    const token = JSON.parse(execFileSync('cmd.exe', [
      '/d', '/s', '/c', 'az account get-access-token --resource https://management.azure.com/ --output json',
    ], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] })).accessToken;
    const groupPath = `/subscriptions/${config.subscription}/resourceGroups/${config.resourceGroup}`;
    apimPath = `${groupPath}/providers/Microsoft.ApiManagement/service/${config.prefix}-apim`;
    arm = async (path, method = 'GET', body, ifMatch) => {
      assert(path.startsWith(`${groupPath}/`) || path.startsWith(`${groupPath}?`), 'ARM scope outside owned group');
      const response = await fetch(`https://management.azure.com${path}`, {
        method, headers: {
          Authorization: `Bearer ${token}`, 'Content-Type': 'application/json',
          ...(ifMatch ? { 'If-Match': ifMatch } : {}),
        },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: AbortSignal.timeout(90_000),
      });
      const text = await response.text();
      const data = text ? JSON.parse(text) : {};
      assert(response.ok, `ARM ${method} ${response.status}: ${data.error?.code ?? 'unknown'}`);
      Object.defineProperty(data, '_etag', { value: response.headers.get('etag') });
      return data;
    };
    const group = await arm(`${groupPath}?api-version=2024-03-01`);
    assert.equal(group.tags.session, config.session, 'Group ownership mismatch');
    assert.equal(group.tags.purpose, 'task001-disposable-feasibility');
    const gateway = await arm(`${apimPath}?api-version=${apiVersion}`);
    assert.equal(gateway.sku.name, 'Developer');
    assert.equal(gateway.sku.capacity, 1);
    const endpoint = `${gateway.properties.gatewayUrl}/openai/v1/responses`;
    result.endpoint = endpoint;
    const secrets = await arm(`${apimPath}/subscriptions/probe-access/listSecrets?api-version=${apiVersion}`, 'POST');
    assert(secrets.primaryKey, 'API subscription credential missing');
    const debug = await arm(`${apimPath}/gateways/managed/listDebugCredentials?api-version=${apiVersion}`, 'POST', {
      apiId: `${apimPath}/apis/affinity-probe`, purposes: ['tracing'], credentialsExpireAfter: 'PT1H',
    });
    assert(debug.token, 'Debug credential missing');

    async function call(session, phase) {
      const ledger = existsSync(ledgerPath) ? readFileSync(ledgerPath, 'utf8') : '';
      const entries = ledger.trim() ? ledger.trim().split('\n').map(line => JSON.parse(line)) : [];
      const lastStart = entries.length ? Date.parse(entries.at(-1).at) : 0;
      const spacing = ['fresh-after-fault', 'existing-cookie-after-fault'].includes(phase) ? 2_000 : 10_000;
      await sleep(Math.max(0, lastStart + spacing - Date.now()));
      assert(Date.now() < deadline, 'Test deadline reached; no request sent');
      const number = nextAttempt(ledger);
      const clientRequestId = randomUUID();
      appendFileSync(ledgerPath, `${JSON.stringify({ number, at: new Date().toISOString(), phase, clientRequestId })}\n`);
      const headers = {
        'Content-Type': 'application/json',
        'Ocp-Apim-Subscription-Key': secrets.primaryKey,
        'Apim-Debug-Authorization': debug.token,
        'x-ms-client-request-id': clientRequestId,
      };
      if (session.cookie) headers.Cookie = session.cookie;
      const body = {
        model: 'gpt-5.4-mini',
        input: session.previous ? 'Repeat the two-word marker from my first message, exactly.' : 'Remember the marker azure affinity. Reply with only that two-word marker.',
        max_output_tokens: 2048, reasoning: { effort: 'none' }, store: phase !== 'stateless',
      };
      if (session.previous) body.previous_response_id = session.previous;
      const response = await new Promise((resolve, reject) => {
        const request = https.request(endpoint, {
          method: 'POST', headers, agent, signal: AbortSignal.timeout(60_000),
        }, incoming => {
          const chunks = [];
          let bytes = 0;
          incoming.on('data', chunk => {
            bytes += chunk.length;
            if (bytes > 2 * 1024 * 1024) incoming.destroy(new Error('Response exceeds probe size bound'));
            else chunks.push(chunk);
          });
          incoming.on('error', reject);
          incoming.on('end', () => resolve({
            status: incoming.statusCode, headers: responseHeaders(incoming.rawHeaders),
            body: Buffer.concat(chunks).toString('utf8'),
          }));
        });
        request.on('error', reject);
        request.end(JSON.stringify(body));
      });
      const text = response.body;
      const { body: data, bodyFormat } = responseBody(response.status, text);
      const record = {
        number, phase, session: session.name, status: response.status,
        backend: response.headers.get('x-lab-backend-id'),
        requestId: response.headers.get('x-probe-request-id'),
        traceId: response.headers.get('apim-trace-id'),
        context: response.headers.get('x-probe-backend-context'),
        requestHost: response.headers.get('x-probe-request-host'),
        errorCode: data.error?.code ?? null,
        errorType: data.error?.type ?? null,
        bodyFormat,
        contentType: response.headers.get('content-type'),
        previousFingerprint: session.previous ? fingerprint(session.previous) : null,
        cookieFingerprint: session.cookie ? fingerprint(session.cookie) : null,
        usage: data.usage ?? null,
      };
      result.calls.push(record);
      writeFileSync(resultPath, JSON.stringify(result, null, 2));
      console.log(JSON.stringify(record));
      const accepted = acceptTurn(session, response.status, data, response.headers);
      if (accepted.advanced) {
        const output = (data.output ?? []).flatMap(item => item.content ?? [])
          .filter(item => item.type === 'output_text').map(item => item.text).join('');
        assert.match(output.toLowerCase(), /azure affinity/, 'Response did not retain the conversation marker');
      }
      return { record, data, headers: response.headers };
    }

    const sessions = [];
    for (let i = 0; i < 16; i++) {
      const session = { name: `session-${i + 1}`, turns: 0, cookie: '', previous: '', backend: '' };
      const response = await call(session, 'healthy-first');
      assert.equal(response.record.status, 200, 'Healthy first turn failed');
      sessions.push(session);
      if (sessions.length >= (failureFocus ? 2 : 4) && new Set(sessions.map(item => item.backend)).size === 2) break;
    }
    assert.equal(new Set(sessions.map(item => item.backend)).size, 2, 'Both backends not observed within sample');
    for (const session of (failureFocus ? [] : sessions.slice(0, 4))) {
      for (let turn = 0; turn < 2; turn++) {
        const response = await call(session, 'healthy-chain');
        assert.equal(response.record.status, 200, 'Healthy chained turn failed');
      }
    }
    result.findings.healthyAffinity = failureFocus ? null : true;
    result.findings.healthyDistribution = Object.fromEntries(members.map(member => [
      member, sessions.filter(session => session.backend === member).length,
    ]));
    const stateless = [];
    for (let i = 0; i < (failureFocus ? 0 : 8); i++) {
      const session = { name: `stateless-${i}`, turns: 0, cookie: '', previous: '', backend: '' };
      const response = await call(session, 'stateless');
      assert.equal(response.record.status, 200, 'Independent stateless request failed');
      stateless.push(session.backend);
    }
    result.findings.statelessDistribution = failureFocus ? null : Object.fromEntries(members.map(member => [
      member, stateless.filter(value => value === member).length,
    ]));
    if (!failureFocus) assert.equal(new Set(stateless).size, 2, 'Stateless sample did not observe both members');

    const pinned = sessions[0];
    const healthy = members.find(member => member !== pinned.backend);
    faultPath = `${apimPath}/backends/${pinned.backend}?api-version=${backendVersion}`;
    originalBackend = await arm(faultPath);
    assert.equal(originalBackend.properties.type, 'Single');
    const originalUrl = new URL(originalBackend.properties.url);
    assert.equal(originalUrl.protocol, 'https:');
    const suffix = pinned.backend === 'backend-a' ? 'a' : 'b';
    assert(originalUrl.hostname.startsWith(`${config.prefix}-${suffix}.`), 'Fault host does not match owned account');
    faultUrl = new URL(originalUrl);
    faultUrl.pathname = '/task001-intentionally-missing/openai/v1';
    const failedProperties = { ...originalBackend.properties, url: faultUrl.toString().replace(/\/$/, '') };
    await arm(faultPath, 'PUT', { properties: failedProperties }, originalBackend._etag);
    result.findings.faultMember = pinned.backend;
    result.findings.faultAppliedAt = new Date().toISOString();
    // Configuration propagation is asynchronous; do not manufacture instant convergence.
    let observedFailure = false;
    for (let attempt = 0; attempt < 8; attempt++) {
      await sleep(15_000);
      const response = await call(pinned, 'pinned-fault-detection');
      if (response.record.status >= 400) { observedFailure = true; break; }
    }
    assert(observedFailure, 'Fault did not propagate inside the bounded window');
    const originalPin = pinned.backend;
    for (let i = 0; i < 3; i++) {
      const fresh = { name: `new-during-fault-${i}`, turns: 0, cookie: '', previous: '', backend: '' };
      const response = await call(fresh, 'fresh-after-fault');
      assert.equal(response.record.status, 200, 'Fresh session failed during detected fault');
      assert.equal(fresh.backend, healthy, 'Fresh session did not use healthy backend');
    }
    const previous = pinned.previous;
    const existing = await call(pinned, 'existing-cookie-after-fault');
    assert(existing.record.status >= 400, 'Pinned conversation silently succeeded during backend fault');
    assert.equal(pinned.previous, previous, 'Failed response advanced the chain');
    result.findings.pinnedHttpFailure = true;
    result.findings.pinnedDestinationPreserved = existing.record.backend
      ? existing.record.backend === originalPin : null;
    result.findings.pinnedErrorCode = existing.record.errorCode;
    result.findings.pinnedObservedBackend = existing.record.backend;

    const current = await arm(faultPath);
    assert.equal(current.properties.url, faultUrl.toString().replace(/\/$/, ''), 'Concurrent backend change; refusing blind restore');
    assert.deepEqual(
      { ...current.properties, url: originalBackend.properties.url },
      originalBackend.properties,
      'Concurrent backend properties changed; refusing blind restore',
    );
    await arm(faultPath, 'PUT', { properties: originalBackend.properties }, current._etag);
    faultUrl = undefined;
    result.findings.restoredAt = new Date().toISOString();
    let recovered = false;
    for (let attempt = 0; attempt < 8; attempt++) {
      await sleep(15_000);
      const response = await call(pinned, 'recovery');
      if (response.record.status === 200) { recovered = true; break; }
    }
    assert(recovered, 'Pinned conversation did not recover inside bounded window');
    result.findings.recovered = true;

    result.findings.complete = true;
    if (result.findings.pinnedDestinationPreserved === false) {
      result.findings.designGate = 'Native affinity reselected a different backend; strict pin preservation is NOT established';
    } else if (result.findings.pinnedDestinationPreserved === null) {
      result.findings.designGate = 'Pinned HTTP failure observed without member identity; inspect debug/workspace evidence before deciding preservation';
    }
  } catch (error) {
    result.failure = { name: error.name, message: error.message, causeCode: error.cause?.code ?? error.code ?? null };
    process.exitCode = 1;
    console.error(JSON.stringify(result.failure));
  } finally {
    agent.destroy();
    if (originalBackend && faultUrl && arm) {
      try {
        const current = await arm(faultPath);
        assert.equal(current.properties.url, faultUrl.toString().replace(/\/$/, ''), 'Concurrent backend change; refusing blind restore');
        assert.deepEqual(
          { ...current.properties, url: originalBackend.properties.url },
          originalBackend.properties,
          'Concurrent backend properties changed; refusing blind restore',
        );
        await arm(faultPath, 'PUT', { properties: originalBackend.properties }, current._etag);
        result.findings.restoredAfterFailure = true;
      } catch (error) {
        result.restoreFailure = { name: error.name, message: error.message };
        process.exitCode = 1;
        console.error(JSON.stringify({ restoreFailure: result.restoreFailure }));
      }
    }
    if (arm && apimPath) {
      for (const record of result.calls.filter(call => call.traceId)) {
        try {
          const trace = await arm(`${apimPath}/gateways/managed/listTrace?api-version=${apiVersion}`, 'POST', { traceId: record.traceId });
          const backendSteps = trace.traceEntries?.backend ?? [];
          const safe = backendSteps.flatMap(step => {
            const request = step.data?.request;
            if (!request?.url) return [];
            const url = new URL(request.url);
            return [{ source: step.source, host: url.hostname, path: url.pathname }];
          });
          result.traces.push({
            requestId: record.requestId, traceId: record.traceId,
            stages: Object.keys(trace.traceEntries ?? {}), backendSteps: safe,
            stepSources: Object.values(trace.traceEntries ?? {}).flatMap(steps => steps.map(step => step.source)),
          });
        } catch (error) {
          result.traces.push({ requestId: record.requestId, retrievalError: error.message });
          process.exitCode = 1;
          console.error(JSON.stringify({ traceRetrievalError: error.message }));
        }
      }
      result.findings.debugTraceCount = result.traces.filter(trace => !trace.retrievalError).length;
    }
    result.finished = new Date().toISOString();
    writeFileSync(resultPath, JSON.stringify(result, null, 2));
    closeSync(lock);
    unlinkSync(lockPath);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  assert(process.argv[2], 'Usage: node probe.mjs <ignored nonsecret config.json>');
  assert(!process.argv[3] || process.argv[3] === '--focus-failure', 'Unknown probe option');
  await main(process.argv[2], process.argv[3] === '--focus-failure');
}
