using './main.bicep'

param environmentName = readEnvironmentVariable('AZURE_ENV_NAME')
param location = readEnvironmentVariable('AZURE_LOCATION')
param publisherName = readEnvironmentVariable('LAB_PUBLISHER_NAME')
param publisherEmail = readEnvironmentVariable('LAB_PUBLISHER_EMAIL')
param modelName = readEnvironmentVariable('LAB_MODEL_NAME', 'gpt-5.4-mini')
param modelVersion = readEnvironmentVariable('LAB_MODEL_VERSION', '2026-03-17')
param modelDeploymentName = readEnvironmentVariable('LAB_MODEL_DEPLOYMENT', 'responses-chat')
param modelDeploymentSku = readEnvironmentVariable('LAB_MODEL_SKU', 'GlobalStandard')
param modelCapacity = int(readEnvironmentVariable('LAB_MODEL_CAPACITY', '20'))
param sessionAffinityEnabled = bool(readEnvironmentVariable('LAB_SESSION_AFFINITY_ENABLED', 'true'))
param tags = {
  'lab-owner': readEnvironmentVariable('LAB_OWNER_ID')
}
