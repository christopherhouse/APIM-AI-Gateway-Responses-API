targetScope = 'resourceGroup'

@minLength(6)
@maxLength(16)
param prefix string
param expiresUtc string
param sessionId string
param publisherEmail string

var location = 'westus3'
var tags = {
  purpose: 'task001-disposable-feasibility'
  session: sessionId
  expiresUtc: expiresUtc
  SecurityControl: 'Ignore'
}
var roleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: 1 }
  }
}

resource accounts 'Microsoft.CognitiveServices/accounts@2025-06-01' = [for suffix in ['a', 'b']: {
  name: '${prefix}-${suffix}'
  location: location
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: '${prefix}-${suffix}'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    networkAcls: { defaultAction: 'Allow' }
  }
}]

resource models 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = [for i in range(0, 2): {
  parent: accounts[i]
  name: 'gpt-5.4-mini'
  sku: { name: 'GlobalStandard', capacity: 20 }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-5.4-mini', version: '2026-03-17' }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}]

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: '${prefix}-apim'
  location: location
  tags: tags
  sku: { name: 'Developer', capacity: 1 }
  identity: { type: 'SystemAssigned' }
  properties: {
    publisherEmail: publisherEmail
    publisherName: 'Disposable feasibility lab'
    publicNetworkAccess: 'Enabled'
    virtualNetworkType: 'None'
    customProperties: {
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls10': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls11': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Tls10': 'false'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Tls11': 'false'
    }
  }
}

resource assignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, 2): {
  name: guid(accounts[i].id, apim.id, roleId)
  scope: accounts[i]
  properties: {
    roleDefinitionId: roleId
    principalId: apim.identity.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource backends 'Microsoft.ApiManagement/service/backends@2025-03-01-preview' = [for i in range(0, 2): {
  parent: apim
  name: i == 0 ? 'backend-a' : 'backend-b'
  properties: {
    type: 'Single'
    protocol: 'http'
    url: uri(accounts[i].properties.endpoints['OpenAI Language Model Instance API'], 'openai/v1')
    tls: { validateCertificateChain: true, validateCertificateName: true }
    circuitBreaker: {
      rules: [{
        name: 'probe-breaker'
        failureCondition: {
          count: 1
          interval: 'PT1M'
          statusCodeRanges: [
            { min: 404, max: 404 }
            { min: 429, max: 429 }
            { min: 500, max: 599 }
          ]
        }
        tripDuration: 'PT30S'
        acceptRetryAfter: true
      }]
    }
  }
}]

resource pool 'Microsoft.ApiManagement/service/backends@2025-03-01-preview' = {
  parent: apim
  name: 'probe-pool'
  properties: {
    type: 'Pool'
    pool: {
      services: [for i in range(0, 2): { id: backends[i].id, priority: 1, weight: 1 }]
      sessionAffinity: { sessionId: { name: 'APIM-Backend-Affinity', source: 'cookie' } }
    }
  }
}

resource api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: 'affinity-probe'
  properties: {
    displayName: 'Task 001 feasibility only'
    path: 'openai/v1'
    protocols: ['https']
    subscriptionRequired: true
  }
}

resource operation 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  parent: api
  name: 'responses'
  properties: {
    displayName: 'Responses'
    method: 'POST'
    urlTemplate: '/responses'
    responses: []
  }
}

resource policy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: replace(replace(loadTextContent('policy.xml'), '__HOST_A__', first(split(replace(accounts[0].properties.endpoints['OpenAI Language Model Instance API'], 'https://', ''), '/'))), '__HOST_B__', first(split(replace(accounts[1].properties.endpoints['OpenAI Language Model Instance API'], 'https://', ''), '/')))
  }
  dependsOn: [pool, assignments]
}

resource access 'Microsoft.ApiManagement/service/subscriptions@2024-05-01' = {
  parent: apim
  name: 'probe-access'
  properties: {
    displayName: 'Temporary API-scoped probe access'
    scope: api.id
    state: 'active'
    allowTracing: false
  }
}

resource gatewayLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'probe-logs'
  scope: apim
  properties: {
    workspaceId: workspace.id
    logAnalyticsDestinationType: 'Dedicated'
    logs: [{ category: 'GatewayLogs', enabled: true }]
  }
}

resource logger 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  parent: apim
  name: 'azuremonitor'
  properties: {
    loggerType: 'azureMonitor'
    isBuffered: true
  }
}

resource diagnostic 'Microsoft.ApiManagement/service/apis/diagnostics@2024-05-01' = {
  parent: api
  name: 'azuremonitor'
  properties: {
    loggerId: logger.id
    alwaysLog: 'allErrors'
    logClientIp: false
    verbosity: 'information'
    sampling: { samplingType: 'fixed', percentage: 100 }
    frontend: {
      request: { headers: [], body: { bytes: 0 } }
      response: { headers: [], body: { bytes: 0 } }
    }
    backend: {
      request: { headers: [], body: { bytes: 0 } }
      response: { headers: [], body: { bytes: 0 } }
    }
  }
}

output apimId string = apim.id
output apiId string = api.id
output gatewayUrl string = '${apim.properties.gatewayUrl}/openai/v1/responses'
output workspaceCustomerId string = workspace.properties.customerId
output backendIds array = [for i in range(0, 2): backends[i].id]
output accountNames array = [for i in range(0, 2): accounts[i].name]
