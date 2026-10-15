import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';

const config = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const result = JSON.parse(readFileSync(process.argv[3], 'utf8'));
const workspace = process.argv[4];
assert.match(workspace, /^[a-f0-9-]{36}$/);
const requestIds = result.calls.map(call => call.requestId).filter(Boolean);
assert(requestIds.length, 'No gateway request IDs to correlate');
for (const id of requestIds) assert.match(id, /^[a-f0-9-]{36}$/);
const deadline = Math.min(Date.now() + 10 * 60_000, Date.parse(config.expiresUtc) - 10 * 60_000);
assert(Date.now() < deadline, 'No time remains for ingestion polling before cleanup');
const token = JSON.parse(execFileSync('cmd.exe', [
  '/d', '/s', '/c', 'az account get-access-token --resource https://api.loganalytics.io/ --output json',
], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] })).accessToken;
const query = `ApiManagementGatewayLogs
| where TimeGenerated > ago(3h)
| where tostring(CorrelationId) in (${requestIds.map(id => `"${id}"`).join(',')})
| project TimeGenerated, CorrelationId, BackendId, BackendUrl, ResponseCode, BackendResponseCode, TraceRecords
| order by TimeGenerated asc`;
let lastStatus;
while (Date.now() < deadline) {
  const response = await fetch(`https://api.loganalytics.azure.com/v1/workspaces/${workspace}/query`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ query, timespan: 'PT3H' }),
    signal: AbortSignal.timeout(60_000),
  });
  const data = await response.json();
  lastStatus = { http: response.status, code: data.error?.code ?? null };
  if (response.ok) {
    const table = data.tables.find(table => table.name === 'PrimaryResult');
    assert(table, 'Log query returned no PrimaryResult table');
    const rows = table.rows.map(row => Object.fromEntries(table.columns.map((column, i) => [column.name, row[i]])));
    const matched = new Set(rows.map(row => row.CorrelationId.toLowerCase()));
    const traced = rows.filter(row => {
      const traces = typeof row.TraceRecords === 'string' ? JSON.parse(row.TraceRecords || '[]') : row.TraceRecords;
      return traces && JSON.stringify(traces).includes('affinity-probe');
    });
    const evidence = {
      collected: new Date().toISOString(), query,
      expectedRequestCount: new Set(requestIds).size,
      correlatedRequestCount: matched.size, policyTraceRowCount: traced.length, rows,
    };
    writeFileSync('artifacts\\001-workspace-evidence.json', JSON.stringify(evidence, null, 2));
    console.log(JSON.stringify({
      correlatedRequests: matched.size, expectedRequests: new Set(requestIds).size,
      policyTraceRows: traced.length,
    }));
    if (requestIds.every(id => matched.has(id.toLowerCase())) && traced.length) process.exit(0);
  } else {
    console.error(JSON.stringify({ ingestionQueryNotReady: lastStatus }));
    assert([400, 429, 503, 504].includes(response.status), `Workspace query failed: HTTP ${response.status}`);
  }
  await sleep(30_000);
}
throw new Error(`Ingestion evidence incomplete inside ten-minute budget: ${JSON.stringify(lastStatus)}`);
