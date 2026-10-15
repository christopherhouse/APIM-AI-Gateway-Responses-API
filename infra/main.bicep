targetScope = 'resourceGroup'

@description('Lab environment identifier. Use lowercase letters, digits, and interior hyphens; start and end with a letter or digit.')
@minLength(2)
@maxLength(16)
param environmentName string

@description('Approved resource placement. GlobalStandard inference processing is not region-bound.')
@allowed([
  'westus3'
])
param location string = 'westus3'

@description('APIM publisher display name. Supply locally; do not commit personal metadata.')
@minLength(1)
param publisherName string

@description('APIM publisher email. Supply locally; do not commit personal metadata.')
@minLength(1)
param publisherEmail string

@description('Globally unique gateway name. Use Azure-compatible letters, digits, and interior hyphens.')
@minLength(1)
@maxLength(50)
param apimName string = 'apim-${environmentName}-${uniqueString(resourceGroup().id, environmentName)}'

@description('Globally unique Foundry A account and custom subdomain name.')
@minLength(2)
@maxLength(64)
param foundryAName string = 'ai-${environmentName}-${uniqueString(resourceGroup().id, environmentName)}-a'

@description('Globally unique Foundry B account and custom subdomain name; must differ from A.')
@minLength(2)
@maxLength(64)
param foundryBName string = 'ai-${environmentName}-${uniqueString(resourceGroup().id, environmentName)}-b'

@description('Log Analytics workspace name.')
@minLength(4)
@maxLength(63)
param workspaceName string = 'log-${environmentName}-${uniqueString(resourceGroup().id, environmentName)}'

@description('Verified model; overrides require fresh model, version, region, and quota checks.')
@minLength(1)
param modelName string = 'gpt-5.4-mini'

@description('Explicit model version; NoAutoUpgrade does not prevent model retirement.')
@minLength(1)
param modelVersion string = '2026-03-17'

@description('Identical deployment name in both accounts.')
@minLength(1)
@maxLength(64)
param modelDeploymentName string = 'responses-chat'

@description('Native routing cookie name, not its opaque value. Keep clients aligned with this output.')
@allowed([
  'APIM-Backend-Affinity'
])
param affinityCookieName string = 'APIM-Backend-Affinity'

@description('Selected global deployment SKU. Resource placement remains West US 3.')
@allowed([
  'GlobalStandard'
])
param modelDeploymentSku string = 'GlobalStandard'

@description('Capacity per backend, not a token-spending cap. Only 20 has live feasibility evidence; recheck other values before deployment.')
@minValue(1)
@maxValue(20)
param modelCapacity int = 20

@description('Workspace default retention. Individual table rules can differ and require runtime verification.')
@minValue(30)
@maxValue(90)
param logRetentionDays int = 30

@description('Workspace ingestion cap in GB/day; can overshoot and is not a billing cap.')
@minValue(1)
@maxValue(5)
param logDailyQuotaGb int = 1

@description('Additional approved resource tags. Required lab ownership tags cannot be overridden.')
param tags object = {}

var labTags = union(tags, {
  'azd-env-name': environmentName
  purpose: 'apim-responses-affinity-lab'
})
var categories = loadJsonContent('diagnostic-categories.json')
var accountNames = [
  foundryAName
  foundryBName
]
var gatewayIdentityName = 'id-${environmentName}-${uniqueString(resourceGroup().id, environmentName)}'
var modelDeployments = [
  {
    name: modelDeploymentName
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
    sku: {
      name: modelDeploymentSku
      capacity: modelCapacity
    }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
]

module workspace 'br/public:avm/res/operational-insights/workspace:0.16.1' = {
  name: 'foundation-workspace'
  params: {
    name: workspaceName
    location: location
    tags: labTags
    enableTelemetry: false
    skuName: 'PerGB2018'
    dataRetention: logRetentionDays
    dailyQuotaGb: string(logDailyQuotaGb)
    forceCmkForQuery: false
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    features: {
      disableLocalAuth: true
      enableLogAccessUsingOnlyResourcePermissions: false
      enableDataExport: false
    }
    diagnosticSettings: [
      {
        name: 'lab-workspace'
        useThisWorkspace: true
        logAnalyticsDestinationType: 'Dedicated'
        logCategoriesAndGroups: [for category in categories.workspace.logs: { category: category }]
        metricCategories: [for category in categories.workspace.metrics: { category: category }]
      }
    ]
  }
}

module gatewayIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  name: 'foundation-gateway-identity'
  params: {
    name: gatewayIdentityName
    location: location
    tags: labTags
    enableTelemetry: false
  }
}

module apim 'br/public:avm/res/api-management/service:0.15.0' = {
  name: 'foundation-apim'
  params: {
    name: apimName
    location: location
    tags: labTags
    enableTelemetry: false
    publisherName: publisherName
    publisherEmail: publisherEmail
    sku: 'Developer'
    skuCapacity: 1
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [gatewayIdentity.outputs.resourceId]
    }
    publicNetworkAccess: 'Enabled'
    virtualNetworkType: 'None'
    customProperties: {
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Ciphers.TripleDes168': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Ciphers.TLS_RSA_WITH_AES_128_CBC_SHA': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Ciphers.TLS_RSA_WITH_AES_256_CBC_SHA': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Ciphers.TLS_RSA_WITH_AES_128_CBC_SHA256': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Ciphers.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Ciphers.TLS_RSA_WITH_AES_256_CBC_SHA256': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Ciphers.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Ciphers.TLS_RSA_WITH_AES_128_GCM_SHA256': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls10': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls11': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Tls10': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Tls11': 'false'
    }
    diagnosticSettings: [
      {
        name: 'lab-workspace'
        workspaceResourceId: workspace.outputs.resourceId
        logAnalyticsDestinationType: 'Dedicated'
        logCategoriesAndGroups: [for category in categories.apim.logs: { category: category }]
        metricCategories: [for category in categories.apim.metrics: { category: category }]
      }
    ]
  }
}

module foundries 'br/public:avm/res/cognitive-services/account:0.13.2' = [
  for (name, i) in accountNames: {
    name: 'foundation-foundry-${i}'
    params: {
      name: name
      location: location
      tags: labTags
      enableTelemetry: false
      kind: 'AIServices'
      sku: 'S0'
      customSubDomainName: name
      publicNetworkAccess: 'Enabled'
      networkAcls: { defaultAction: 'Allow' }
      disableLocalAuth: true
      deployments: modelDeployments
      diagnosticSettings: [
        {
          name: 'lab-workspace'
          workspaceResourceId: workspace.outputs.resourceId
          logAnalyticsDestinationType: 'AzureDiagnostics'
          logCategoriesAndGroups: [for category in categories.foundry.logs: { category: category }]
          metricCategories: [for category in categories.foundry.metrics: { category: category }]
        }
      ]
    }
  }
]

resource gateway 'Microsoft.ApiManagement/service@2024-05-01' existing = {
  name: apimName
  dependsOn: [apim]
}

resource accounts 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = [
  for name in accountNames: {
    name: name
    dependsOn: [foundries]
  }
]

// Keep the runtime principal out of AVM's non-null nested parameter validation.
resource gatewayGrants 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (name, i) in accountNames: {
    name: guid(accounts[i].id, apimName, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    scope: accounts[i]
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
      principalId: gatewayIdentity.outputs.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

module routing 'gateway.bicep' = {
  name: 'gateway-routing'
  params: {
    apimName: apimName
    managedIdentityClientId: gatewayIdentity.outputs.clientId
    foundryAEndpoint: accounts[0].properties.endpoints['OpenAI Language Model Instance API']
    foundryBEndpoint: accounts[1].properties.endpoints['OpenAI Language Model Instance API']
    modelDeploymentName: modelDeploymentName
    affinityCookieName: affinityCookieName
  }
  dependsOn: [apim, foundries, gatewayGrants]
}

@description('Existing externally managed deployment group; this template does not create it.')
output resourceGroupName string = resourceGroup().name
output apimResourceId string = apim.outputs.resourceId
output apimGatewayUrl string = gateway.properties.gatewayUrl
output apimPrincipalId string = gatewayIdentity.outputs.principalId
output apimIdentityResourceId string = gatewayIdentity.outputs.resourceId
output foundryAResourceId string = foundries[0].outputs.resourceId
output foundryBResourceId string = foundries[1].outputs.resourceId
output foundryAEndpoint string = accounts[0].properties.endpoints['OpenAI Language Model Instance API']
output foundryBEndpoint string = accounts[1].properties.endpoints['OpenAI Language Model Instance API']
output workspaceResourceId string = workspace.outputs.resourceId
output workspaceCustomerId string = workspace.outputs.logAnalyticsWorkspaceId
output modelDeploymentName string = modelDeploymentName
output responsesEndpoint string = '${gateway.properties.gatewayUrl}/${routing.outputs.apiPath}/responses'
output backendAId string = routing.outputs.backendAId
output backendBId string = routing.outputs.backendBId
output apimSubscriptionResourceId string = routing.outputs.apimSubscriptionResourceId
output affinityCookieName string = affinityCookieName
