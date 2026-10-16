import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';

const template = JSON.parse(readFileSync(new URL('../artifacts/002-template.json', import.meta.url), 'utf8'));
const routing = template.resources.routing;
const gateway = routing.properties.template;
const resources = Object.values(gateway.resources);
const byType = type => resources.filter(resource => resource.type === `Microsoft.ApiManagement/service/${type}`);
const [backends, pool] = byType('backends');
const [api] = byType('apis');
const [operation] = byType('apis/operations');
const [policy] = byType('apis/policies');
const [access] = byType('subscriptions');
const [logger] = byType('loggers');
const [diagnostic] = byType('apis/diagnostics');

test('routing waits for both accounts, models and identity grants and consumes actual endpoints', () => {
  assert.deepEqual(routing.dependsOn.sort(), [
    "[format('accounts[{0}]', 0)]", "[format('accounts[{0}]', 1)]", 'apim', 'foundries', 'gatewayGrants', 'gatewayIdentity',
  ]);
  for (const [index, name] of [[0, 'foundryAEndpoint'], [1, 'foundryBEndpoint']]) {
    const endpoint = routing.properties.parameters[name].value;
    assert.match(endpoint, new RegExp(`accounts.*${index}`));
    assert.match(endpoint, /\.endpoints\['OpenAI Language Model Instance API'\]/);
  }
  assert.equal(routing.properties.parameters.modelDeploymentName.value, "[parameters('modelDeploymentName')]");
  assert.equal(routing.properties.parameters.managedIdentityClientId.value, "[reference('gatewayIdentity').outputs.clientId.value]");
  assert.equal(routing.properties.parameters.affinityCookieName.value, "[parameters('affinityCookieName')]");
  assert.equal(routing.properties.parameters.sessionAffinityEnabled.value, "[parameters('sessionAffinityEnabled')]");
  assert.equal(template.parameters.sessionAffinityEnabled.defaultValue, true);
  assert.deepEqual(template.outputs.sessionAffinityEnabled, { type: 'bool', value: "[parameters('sessionAffinityEnabled')]" });
  assert.deepEqual(template.parameters.affinityCookieName.allowedValues, ['APIM-Backend-Affinity']);
  assert.equal(resources.length, 8);
});

test('two TLS-validated members have the verified breakers and equal native cookie-affinity weights', () => {
  assert.equal(backends.apiVersion, '2025-03-01-preview');
  assert.equal(pool.apiVersion, backends.apiVersion);
  assert.equal(backends.copy.count, "[length(variables('endpoints'))]");
  assert.deepEqual(gateway.variables.labels, ['backend-a', 'backend-b']);
  assert.equal(backends.properties.url, "[uri(variables('endpoints')[copyIndex()], 'openai/v1')]");
  assert.equal(backends.properties.type, 'Single');
  assert.deepEqual(backends.properties.tls, { validateCertificateChain: true, validateCertificateName: true });
  assert.deepEqual(backends.properties.circuitBreaker.rules, [{
    name: 'responses-breaker',
    failureCondition: {
      count: 1, interval: 'PT1M',
      statusCodeRanges: [{ min: 404, max: 404 }, { min: 429, max: 429 }, { min: 500, max: 599 }],
    },
    tripDuration: 'PT30S', acceptRetryAfter: true,
  }]);
  assert.equal(pool.properties.type, 'Pool');
  const services = gateway.variables.copy.find(item => item.name === 'poolServices');
  assert.equal(services.count, '[length(range(0, 2))]');
  assert.equal(services.input.priority, 1);
  assert.equal(services.input.weight, 1);
  assert.match(services.input.id, /resourceId\('Microsoft\.ApiManagement\/service\/backends'/);
  for (const enabled of [true, false]) {
    const actual = runInNewContext(pool.properties.pool.slice(1, -1).replace(/\bif\(/g, 'choose('), {
      parameters: name => ({ sessionAffinityEnabled: enabled, affinityCookieName: 'APIM-Backend-Affinity' })[name],
      variables: name => {
        assert.equal(name, 'poolServices');
        return ['backend-a', 'backend-b'];
      },
      union: (...objects) => Object.assign({}, ...objects),
      choose: (condition, yes, no) => condition ? yes : no,
      createObject: (...pairs) => Object.fromEntries(Array.from({ length: pairs.length / 2 }, (_, i) => pairs.slice(i * 2, i * 2 + 2))),
    });
    assert.deepEqual(actual.services, ['backend-a', 'backend-b']);
    if (enabled)
      assert.deepEqual(actual.sessionAffinity.sessionId, { name: 'APIM-Backend-Affinity', source: 'cookie' });
    else
      assert.equal(Object.hasOwn(actual, 'sessionAffinity'), false);
  }
});

test('only HTTPS POST Responses is published, with API-scoped header subscription access', () => {
  assert.deepEqual(api.properties.protocols, ['https']);
  assert.equal(api.properties.path, 'openai/v1');
  assert.equal(api.properties.subscriptionRequired, true);
  assert.equal(api.properties.subscriptionKeyParameterNames.header, 'Ocp-Apim-Subscription-Key');
  assert.equal(operation.properties.method, 'POST');
  assert.equal(operation.properties.urlTemplate, '/responses');
  assert.equal(byType('apis/operations').length, 1);
  assert.equal(access.properties.scope, "[resourceId('Microsoft.ApiManagement/service/apis', parameters('apimName'), 'responses')]");
  assert.equal(access.properties.allowTracing, false);
  assert.equal(access.properties.state, 'active');
  assert.equal(access.properties.primaryKey, undefined);
  assert.equal(access.properties.secondaryKey, undefined);
});

test('policy is deployed after pool and operation with allow-listed host and model substitutions', () => {
  assert.equal(policy.properties.format, 'rawxml');
  assert.ok(policy.dependsOn.includes("[resourceId('Microsoft.ApiManagement/service/backends', parameters('apimName'), 'responses-pool')]"));
  assert.ok(policy.dependsOn.includes("[resourceId('Microsoft.ApiManagement/service/apis/operations', parameters('apimName'), 'responses', 'responses')]"));
  const expression = gateway.variables.policyXml;
  const sourceVariable = /variables\('([^']+)'\)/.exec(expression)[1];
  assert.equal(gateway.variables[sourceVariable].replace(/\r\n/g, '\n'),
    readFileSync(new URL('./policies/responses.xml', import.meta.url), 'utf8').replace(/\r\n/g, '\n'));
  assert.match(expression, /loadTextContent|variables\(/);
  assert.match(expression, /parseUri\(parameters\('foundryAEndpoint'\)\)\.host/);
  assert.match(expression, /parseUri\(parameters\('foundryBEndpoint'\)\)\.host/);
  assert.match(expression, /__MODEL_DEPLOYMENT__/);
  assert.match(expression, /&amp;/);
  assert.match(expression, /&quot;/);
  assert.match(expression, /&lt;/);
  assert.match(expression, /__AFFINITY_COOKIE__/);
  assert.match(expression, /parameters\('affinityCookieName'\)/);
  assert.match(expression, /__MANAGED_IDENTITY_CLIENT_ID__/);
  assert.match(expression, /parameters\('managedIdentityClientId'\)/);
  assert.equal(policy.properties.value, "[variables('configuredPolicyXml')]");
  for (const enabled of [true, false]) {
    const configured = runInNewContext(gateway.variables.configuredPolicyXml.slice(1, -1).replace(/\bif\(/g, 'choose('), {
      variables: name => {
        assert.equal(name, 'policyXml');
        return gateway.variables[sourceVariable];
      },
      parameters: name => {
        assert.equal(name, 'sessionAffinityEnabled');
        return enabled;
      },
      replace: (value, search, replacement) => value.replaceAll(search, replacement),
      choose: (condition, yes, no) => condition ? yes : no,
      string: value => typeof value === 'boolean' ? value ? 'True' : 'False' : String(value),
    });
    assert.match(configured, new RegExp(`name="labAffinityEnabled" value="@\\(${enabled}\\)"`));
    assert.doesNotMatch(configured, /__SESSION_AFFINITY_ENABLED__/);
  }
});

test('native Monitor trace diagnostics sample all calls without recording secrets, payloads or client IP', () => {
  assert.equal(logger.name, "[format('{0}/{1}', parameters('apimName'), 'azuremonitor')]");
  assert.equal(logger.properties.loggerType, 'azureMonitor');
  assert.ok(diagnostic.dependsOn.includes("[resourceId('Microsoft.ApiManagement/service/loggers', parameters('apimName'), 'azuremonitor')]"));
  assert.equal(diagnostic.properties.loggerId, "[resourceId('Microsoft.ApiManagement/service/loggers', parameters('apimName'), 'azuremonitor')]");
  assert.equal(diagnostic.properties.alwaysLog, 'allErrors');
  assert.equal(diagnostic.properties.verbosity, 'information');
  assert.equal(diagnostic.properties.logClientIp, false);
  assert.deepEqual(diagnostic.properties.sampling, { samplingType: 'fixed', percentage: 100 });
  for (const side of ['frontend', 'backend']) {
    for (const direction of ['request', 'response']) {
      const capture = diagnostic.properties[side][direction];
      assert.equal(capture.body.bytes, 0);
      assert.deepEqual(capture.headers, side === 'frontend' && direction === 'response'
        ? ['X-Lab-Request-Id', 'X-Lab-Backend-Id'] : []);
    }
  }
  assert.equal(diagnostic.properties.largeLanguageModel, undefined);
  assert.equal(gateway.outputs.apimSubscriptionResourceId.type, 'string');
  assert.doesNotMatch(JSON.stringify(gateway.outputs), /listSecrets|listKeys|primaryKey|secondaryKey/i);
});
