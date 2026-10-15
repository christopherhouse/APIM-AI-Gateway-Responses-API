import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const template = JSON.parse(readFileSync(new URL('../artifacts/002-template.json', import.meta.url), 'utf8'));
const parameters = JSON.parse(readFileSync(new URL('../artifacts/002-parameters.json', import.meta.url), 'utf8'));
const inventory = JSON.parse(readFileSync(new URL('./diagnostic-categories.json', import.meta.url), 'utf8'));
const resources = Object.values(template.resources);
const modules = resources.filter(resource => resource.type === 'Microsoft.Resources/deployments');
const moduleByName = name => modules.find(module => module.name === name);
const workspace = moduleByName('foundation-workspace');
const apim = moduleByName('foundation-apim');
const identity = moduleByName('foundation-gateway-identity');
const foundries = modules.find(module => module.copy?.name === 'foundries');
const value = (module, name) => module.properties.parameters[name].value;

function allTemplates(current) {
  return [current, ...Object.values(current.resources ?? {}).flatMap(resource =>
    resource.properties?.template ? allTemplates(resource.properties.template) : [])];
}

test('foundation stays in its existing group and isolates gateway child resources', () => {
  assert.equal(template.$schema, 'https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#');
  assert.equal(modules.length, 5);
  for (const current of allTemplates(template)) {
    for (const resource of Object.values(current.resources ?? {})) {
      assert.notEqual(resource.type, 'Microsoft.Resources/resourceGroups');
    }
  }
  for (const module of modules.filter(module => module.name !== 'gateway-routing')) {
    assert.equal(module.subscriptionId, undefined);
    assert.equal(module.resourceGroup, undefined);
    assert.equal(value(module, 'location'), "[parameters('location')]");
    assert.equal(value(module, 'enableTelemetry'), false);
    assert.equal(module.properties.mode, 'Incremental');
    for (const option of ['privateEndpoints', 'lock', 'customerManagedKey', 'secretsExportConfiguration']) {
      assert.equal(module.properties.parameters[option], undefined);
    }
  }
  assert.deepEqual(template.parameters.location.allowedValues, ['westus3']);
  assert.equal(template.parameters.location.defaultValue, 'westus3');
  assert.equal(apim.properties.parameters.apis, undefined);
  assert.equal(apim.properties.parameters.backends, undefined);
  assert.equal(apim.properties.parameters.subscriptions, undefined);
});

test('two account-scoped grants connect gateway identity to identical bounded model deployments', () => {
  assert.deepEqual(template.variables.accountNames, [
    "[parameters('foundryAName')]", "[parameters('foundryBName')]",
  ]);
  assert.equal(foundries.copy.count, "[length(variables('accountNames'))]");
  assert.equal(value(foundries, 'deployments'), "[variables('modelDeployments')]");
  assert.equal(template.variables.modelDeployments.length, 1);
  const deployment = template.variables.modelDeployments[0];
  assert.deepEqual(deployment.model, {
    format: 'OpenAI', name: "[parameters('modelName')]", version: "[parameters('modelVersion')]",
  });
  assert.deepEqual(deployment.sku, {
    name: "[parameters('modelDeploymentSku')]", capacity: "[parameters('modelCapacity')]",
  });
  assert.equal(deployment.versionUpgradeOption, 'NoAutoUpgrade');
  assert.equal(template.parameters.modelCapacity.minValue, 1);
  assert.equal(template.parameters.modelCapacity.maxValue, 20);
  assert.equal(value(foundries, 'kind'), 'AIServices');
  assert.equal(value(foundries, 'disableLocalAuth'), true);
  assert.equal(value(foundries, 'publicNetworkAccess'), 'Enabled');
  assert.equal(foundries.properties.parameters.roleAssignments, undefined);
  const grants = resources
    .filter(resource => resource.type === 'Microsoft.Authorization/roleAssignments');
  assert.equal(grants.length, 1);
  assert.equal(grants[0].copy.count, "[length(variables('accountNames'))]");
  assert.match(grants[0].scope, /Microsoft\.CognitiveServices\/accounts/);
  assert.equal(grants[0].properties.principalId, "[reference('gatewayIdentity').outputs.principalId.value]");
  assert.equal(grants[0].properties.principalType, 'ServicePrincipal');
  assert.match(grants[0].properties.roleDefinitionId, /5e0bd9bd-7b93-4f28-af87-19fc36ad61bd/);
  assert.ok(grants[0].dependsOn.includes('gatewayIdentity'));
  assert.ok(grants[0].dependsOn.includes('foundries'));
  assert.ok(template.resources.routing.dependsOn.includes('gatewayGrants'));
});

test('Developer gateway stays single-unit, public, and identity-enabled without restoring retired TLS', () => {
  assert.equal(value(apim, 'sku'), 'Developer');
  assert.equal(value(apim, 'skuCapacity'), 1);
  assert.equal(value(apim, 'publicNetworkAccess'), 'Enabled');
  assert.equal(value(apim, 'virtualNetworkType'), 'None');
  assert.deepEqual(value(apim, 'managedIdentities'), {
    systemAssigned: false,
    userAssignedResourceIds: ["[reference('gatewayIdentity').outputs.resourceId.value]"],
  });
  assert.ok(apim.dependsOn.includes('gatewayIdentity'));
  assert.equal(identity.properties.template.resources.userAssignedIdentity.type, 'Microsoft.ManagedIdentity/userAssignedIdentities');
  assert.equal(template.outputs.apimPrincipalId.value, "[reference('gatewayIdentity').outputs.principalId.value]");
  assert.equal(template.outputs.apimIdentityResourceId.value, "[reference('gatewayIdentity').outputs.resourceId.value]");
  for (const protocol of ['Protocols.Tls10', 'Protocols.Tls11', 'Backend.Protocols.Tls10', 'Backend.Protocols.Tls11']) {
    assert.equal(value(apim, 'customProperties')[`Microsoft.WindowsAzure.ApiManagement.Gateway.Security.${protocol}`], 'false');
  }
});

test('every inventoried parent log and metric category is wired into the same workspace', () => {
  const alias = /^\[variables\('([^']+)'\)\]$/.exec(template.variables.categories);
  assert.deepEqual(alias ? template.variables[alias[1]] : template.variables.categories, inventory);
  for (const [kind, module] of [['apim', apim], ['foundry', foundries], ['workspace', workspace]]) {
    const [diagnostic] = value(module, 'diagnosticSettings');
    assert.ok(inventory[kind].logs.length > 0);
    assert.equal(new Set(inventory[kind].logs).size, inventory[kind].logs.length);
    for (const [property, categories] of [['logCategoriesAndGroups', 'logs'], ['metricCategories', 'metrics']]) {
      const copy = diagnostic.copy.find(item => item.name === property);
      assert.equal(copy.count, `[length(variables('categories').${kind}.${categories})]`);
      assert.equal(copy.input.category, `[variables('categories').${kind}.${categories}[copyIndex('${property}')]]`);
    }
    if (kind === 'workspace') {
      assert.equal(diagnostic.useThisWorkspace, true);
    } else {
      assert.equal(diagnostic.workspaceResourceId, "[reference('workspace').outputs.resourceId.value]");
      assert.ok(module.dependsOn.includes('workspace'));
    }
    assert.equal(diagnostic.logAnalyticsDestinationType, kind === 'foundry' ? 'AzureDiagnostics' : 'Dedicated');
  }
  assert.deepEqual(value(workspace, 'features'), {
    disableLocalAuth: true, enableLogAccessUsingOnlyResourcePermissions: false, enableDataExport: false,
  });
  assert.equal(value(workspace, 'dataRetention'), "[parameters('logRetentionDays')]");
  assert.equal(value(workspace, 'dailyQuotaGb'), "[string(parameters('logDailyQuotaGb'))]");
  assert.equal(template.parameters.logRetentionDays.defaultValue, 30);
  assert.equal(template.parameters.logDailyQuotaGb.defaultValue, 1);
});

test('deployment outputs never expose ordinary keys and endpoint references wait for their modules', () => {
  for (const current of allTemplates(template)) {
    for (const [name, output] of Object.entries(current.outputs ?? {})) {
      if (/listKeys|listSecrets/i.test(JSON.stringify(output.value))) {
        assert.ok(['securestring', 'secureobject'].includes(output.type.toLowerCase()), `Unsecured nested output ${name}`);
      }
    }
  }
  for (const [name, output] of Object.entries(template.outputs)) {
    assert.doesNotMatch(name, /key|secret|token/i);
    assert.doesNotMatch(output.value, /listKeys|listSecrets|primarySharedKey|secondarySharedKey/i);
  }
  assert.equal(template.outputs.apimGatewayUrl.value, "[reference('gateway').gatewayUrl]");
  assert.deepEqual(template.resources.gateway.dependsOn, ['apim']);
  assert.deepEqual(template.resources.accounts.dependsOn, ['foundries']);
  for (const name of ['foundryAEndpoint', 'foundryBEndpoint']) {
    assert.match(template.outputs[name].value, /reference\(.*\.endpoints\['OpenAI Language Model Instance API'\]/);
  }
  assert.ok(template.resources.routing.dependsOn.includes('apim'));
  assert.ok(template.resources.routing.dependsOn.includes('foundries'));
  assert.match(template.outputs.responsesEndpoint.value, /reference\('gateway'\)\.gatewayUrl/);
  for (const output of ['backendAId', 'backendBId', 'apimSubscriptionResourceId']) {
    assert.equal(template.outputs[output].value, `[reference('routing').outputs.${output}.value]`);
  }
});

test('native parameter file compiles against the entry point with placeholder-only operator values', () => {
  assert.equal(parameters.$schema, 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#');
  for (const name of Object.keys(parameters.parameters)) {
    assert.ok(Object.hasOwn(template.parameters, name), `Unknown parameter ${name}`);
  }
  for (const [name, parameter] of Object.entries(template.parameters)) {
    assert.ok(Object.hasOwn(parameter, 'defaultValue') || Object.hasOwn(parameters.parameters, name), `Missing ${name}`);
  }
  assert.equal(parameters.parameters.publisherEmail.value, 'replace-before-deployment@example.invalid');
  assert.equal(parameters.parameters.publisherName.value, 'REPLACE_WITH_LOCAL_PUBLISHER_NAME');
});
