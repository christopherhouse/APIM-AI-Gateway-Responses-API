targetScope = 'resourceGroup'

param apimName string
param managedIdentityClientId string
param foundryAEndpoint string
param foundryBEndpoint string
param modelDeploymentName string

@allowed([
  'APIM-Backend-Affinity'
])
param affinityCookieName string

var endpoints = [
  foundryAEndpoint
  foundryBEndpoint
]
var labels = [
  'backend-a'
  'backend-b'
]
var policyXml = replace(replace(
  replace(
    replace(
      replace(loadTextContent('policies/responses.xml'), '__HOST_A__', toLower(parseUri(foundryAEndpoint).host)),
      '__HOST_B__',
      toLower(parseUri(foundryBEndpoint).host)
    ),
    '__MODEL_DEPLOYMENT__',
    replace(replace(replace(modelDeploymentName, '&', '&amp;'), '"', '&quot;'), '<', '&lt;')
  ),
  '__AFFINITY_COOKIE__',
  affinityCookieName
), '__MANAGED_IDENTITY_CLIENT_ID__', managedIdentityClientId)

resource gateway 'Microsoft.ApiManagement/service@2024-05-01' existing = {
  name: apimName
}

resource backends 'Microsoft.ApiManagement/service/backends@2025-03-01-preview' = [
  for (endpoint, i) in endpoints: {
    parent: gateway
    name: labels[i]
    properties: {
      type: 'Single'
      protocol: 'http'
      url: uri(endpoint, 'openai/v1')
      tls: {
        validateCertificateChain: true
        validateCertificateName: true
      }
      circuitBreaker: {
        rules: [
          {
            name: 'responses-breaker'
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
          }
        ]
      }
    }
  }
]

resource pool 'Microsoft.ApiManagement/service/backends@2025-03-01-preview' = {
  parent: gateway
  name: 'responses-pool'
  properties: {
    type: 'Pool'
    pool: {
      services: [for i in range(0, 2): { id: backends[i].id, priority: 1, weight: 1 }]
      sessionAffinity: {
        sessionId: {
          name: affinityCookieName
          source: 'cookie'
        }
      }
    }
  }
}

resource api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: gateway
  name: 'responses'
  properties: {
    displayName: 'Responses affinity lab'
    path: 'openai/v1'
    protocols: ['https']
    subscriptionRequired: true
    subscriptionKeyParameterNames: {
      header: 'Ocp-Apim-Subscription-Key'
      query: 'subscription-key'
    }
  }
}

resource operation 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  parent: api
  name: 'responses'
  properties: {
    displayName: 'Create response'
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
    value: policyXml
  }
  dependsOn: [pool, operation]
}

resource access 'Microsoft.ApiManagement/service/subscriptions@2024-05-01' = {
  parent: gateway
  name: 'lab-access'
  properties: {
    displayName: 'API-scoped lab access'
    scope: api.id
    state: 'active'
    allowTracing: false
  }
}

resource logger 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  parent: gateway
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
      response: { headers: ['X-Lab-Request-Id', 'X-Lab-Backend-Id'], body: { bytes: 0 } }
    }
    backend: {
      request: { headers: [], body: { bytes: 0 } }
      response: { headers: [], body: { bytes: 0 } }
    }
  }
}

output apiPath string = api.properties.path
output backendAId string = backends[0].id
output backendBId string = backends[1].id
output apimSubscriptionResourceId string = access.id
