#requires -Version 7

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Lab($Condition, [string]$Message) {
    if (-not $Condition) { throw "Lab: $Message" }
}

function Invoke-LabCommand {
    param([string]$Tool, [string[]]$Arguments, [bool]$Json = $true)
    # az.cmd otherwise splits unquoted REST query ampersands on Windows.
    if ($IsWindows -and $Tool -eq 'az') {
        $Arguments = @($Arguments | ForEach-Object {
            if ($_ -match '[&|<>^]') {
                Assert-Lab ($_ -notmatch '["\r\n]') 'Unsafe native argument.'
                '"' + $_ + '"'
            } else { $_ }
        })
    }
    if ($Json -and $Tool -eq 'az') { $Arguments += @('--output', 'json', '--only-show-errors') }
    if (-not $Json) {
        & $Tool @Arguments | Out-Host
        Assert-Lab ($LASTEXITCODE -eq 0) "$Tool $($Arguments[0]) failed; no later action was attempted."
        return
    }
    $raw = & $Tool @Arguments 2>&1
    Assert-Lab ($LASTEXITCODE -eq 0) "$Tool $($Arguments[0]) failed; output withheld to protect credentials. Check sign-in and permissions."
    ($raw -join "`n") | ConvertFrom-Json -AsHashtable
}

function Get-LabContext {
    param([string]$Root, [string]$EnvironmentName, [string]$SubscriptionId, [string]$ResourceGroupName, [switch]$RequireExisting)
    Assert-Lab ($EnvironmentName -cmatch '^[a-z0-9](?:[a-z0-9-]{0,14}[a-z0-9])$') 'Environment must be 2-16 lowercase letters/digits/interior hyphens.'
    Assert-Lab ($SubscriptionId -match '^[0-9a-fA-F-]{36}$' -and [guid]::TryParse($SubscriptionId, [ref][guid]::Empty)) 'Subscription must be an explicit UUID.'
    Assert-Lab ($ResourceGroupName -match '^[a-zA-Z0-9][a-zA-Z0-9_-]{1,63}$') 'Use a 2-64 character dedicated group name containing letters/digits/hyphens/underscores.'
    $directory = Join-Path $Root "artifacts\lab\$EnvironmentName"
    $statePath = Join-Path $directory 'state.json'
    $target = @{
        environment = $EnvironmentName
        subscription = $SubscriptionId.ToLowerInvariant()
        group = $ResourceGroupName
        location = 'westus3'
    }
    $state = @{ target = $target; owner = [guid]::NewGuid().ToString(); retired = $false }
    if (Test-Path -LiteralPath $statePath) {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($key in $target.Keys) {
            Assert-Lab ($state.target[$key] -ceq $target[$key]) "Local environment target changed ($key); use a new environment, never retarget saved state."
        }
        Assert-Lab ($state.owner -match '^[0-9a-f-]{36}$') 'Missing local ownership marker.'
    } else {
        Assert-Lab (-not $RequireExisting) 'Local ownership state is missing; do not adopt an existing group.'
    }
    @{ Root = $Root; Directory = $directory; StatePath = $statePath; State = $state; Target = $target }
}

function Save-LabState($Context) {
    $null = New-Item -ItemType Directory -Path $Context.Directory -Force
    $temporary = $Context.StatePath + '.tmp'
    $Context.State | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporary -Destination $Context.StatePath -Force
}

function Set-LabProcessEnvironment($Context) {
    $values = @{
        AZURE_ENV_NAME = $Context.Target.environment
        AZURE_SUBSCRIPTION_ID = $Context.Target.subscription
        AZURE_RESOURCE_GROUP = $Context.Target.group
        AZURE_LOCATION = $Context.Target.location
        LAB_OWNER_ID = $Context.State.owner
    }
    $previous = @{}
    foreach ($key in $values.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $values[$key], 'Process')
    }
    return $previous
}

function Restore-LabProcessEnvironment([hashtable]$Previous) {
    foreach ($key in $Previous.Keys) {
        if ($null -eq $Previous[$key]) {
            if (Test-Path -LiteralPath "Env:$key") { Remove-Item -LiteralPath "Env:$key" }
        } else {
            [Environment]::SetEnvironmentVariable($key, $Previous[$key], 'Process')
        }
    }
}

function Get-LabGroupId($Context) {
    "/subscriptions/$($Context.Target.subscription)/resourceGroups/$($Context.Target.group)"
}

function Get-LabRest([string]$Path) {
    Assert-Lab ($Path.StartsWith('/subscriptions/')) 'Only subscription management paths are permitted.'
    Invoke-LabCommand az @('rest', '--method', 'get', '--url', "https://management.azure.com$Path")
}

function Get-LabRestList([string]$Path) {
    Assert-Lab ($Path -match '^(/subscriptions/[0-9a-fA-F-]{36}/)') 'Missing pagination subscription scope.'
    $subscriptionPrefix = $Matches[1]
    $result = Get-LabRest $Path
    $items = @($result.value)
    while ($result['nextLink']) {
        $next = [uri]$result['nextLink']
        Assert-Lab ($next.Scheme -eq 'https' -and $next.Host -eq 'management.azure.com' -and $next.AbsolutePath.StartsWith($subscriptionPrefix, [StringComparison]::OrdinalIgnoreCase)) 'Unexpected management pagination destination.'
        $result = Get-LabRest $next.PathAndQuery
        $items += @($result.value)
    }
    return $items
}

function Assert-LabAccount($Context) {
    $account = Invoke-LabCommand az @('account', 'show')
    Assert-Lab ($account.id -eq $Context.Target.subscription) 'Active Azure CLI subscription differs from the explicit target. No subscription was switched.'
    Assert-Lab ($account.environmentName -eq 'AzureCloud' -and $account.state -eq 'Enabled') 'Require an enabled AzureCloud subscription.'
    Write-Host "Target: $($account.name) [$($account.id)] / $($Context.Target.group) / westus3"
    return $account
}

function Get-LabOwnedResources($Context, [switch]$AllowMissing) {
    $exists = Invoke-LabCommand az @('group', 'exists', '--subscription', $Context.Target.subscription, '--name', $Context.Target.group)
    if (-not $exists) {
        Assert-Lab $AllowMissing 'Dedicated group does not exist.'
        return $null
    }
    $group = Invoke-LabCommand az @('group', 'show', '--subscription', $Context.Target.subscription, '--name', $Context.Target.group)
    Assert-Lab ($group.id -eq (Get-LabGroupId $Context) -and $group.location -eq 'westus3') 'Group identity/location mismatch.'
    Assert-Lab ($group['tags'] -and $group.tags['azd-env-name'] -ceq $Context.Target.environment -and $group.tags['purpose'] -ceq 'apim-responses-affinity-lab' -and $group.tags['lab-owner'] -ceq $Context.State.owner) 'Group is not owned by this local lab environment; refusing adoption.'
    $resources = @(Invoke-LabCommand az @('resource', 'list', '--subscription', $Context.Target.subscription, '--resource-group', $Context.Target.group))
    $limits = @{ 'Microsoft.ApiManagement/service' = 1; 'Microsoft.CognitiveServices/accounts' = 2; 'Microsoft.OperationalInsights/workspaces' = 1 }
    foreach ($resource in $resources) {
        Assert-Lab ($limits.ContainsKey($resource.type)) "Unexpected resource type $($resource.type); refusing reuse/deletion."
        Assert-Lab ($resource.id.StartsWith((Get-LabGroupId $Context) + '/', [StringComparison]::OrdinalIgnoreCase)) 'Resource outside the exact group.'
        Assert-Lab ($resource['tags'] -and $resource.tags['lab-owner'] -ceq $Context.State.owner -and $resource.tags['purpose'] -ceq 'apim-responses-affinity-lab' -and $resource.tags['azd-env-name'] -ceq $Context.Target.environment) 'An inventory resource lacks this environment ownership.'
        if ($Context.State['outputs']) {
            $expected = @('LAB_APIM_RESOURCE_ID', 'LAB_FOUNDRY_A_RESOURCE_ID', 'LAB_FOUNDRY_B_RESOURCE_ID', 'LAB_WORKSPACE_RESOURCE_ID') | ForEach-Object { $Context.State.outputs[$_] }
            Assert-Lab ($resource.id -in $expected) 'Resource ID differs from the last successful deployment inventory.'
        }
        $limits[$resource.type]--
        Assert-Lab ($limits[$resource.type] -ge 0) 'Unexpected additional lab resource; refusing reuse/deletion.'
    }
    return @{ group = $group; resources = $resources }
}

function Assert-LabPermission($Context, [string]$Scope, [string[]]$Actions) {
    $permissions = @(Get-LabRestList "$Scope/providers/Microsoft.Authorization/permissions?api-version=2022-04-01")
    foreach ($action in $Actions) {
        $grants = @($permissions | Where-Object {
            $permission = $_
            @($permission.actions | Where-Object { $action -like $_ }).Count -gt 0 -and
            @($permission.notActions | Where-Object { $action -like $_ }).Count -eq 0
        })
        Assert-Lab ($grants.Count -gt 0) "No effective allow for $action at $Scope. Provider validation must also pass; permissions do not override deny assignments/policy."
    }
}

function Get-LabParameters($Context) {
    foreach ($name in @('LAB_PUBLISHER_NAME', 'LAB_PUBLISHER_EMAIL')) {
        $value = [Environment]::GetEnvironmentVariable($name, 'Process')
        Assert-Lab (-not [string]::IsNullOrWhiteSpace($value) -and $value -notmatch 'REPLACE|example\.invalid|[\r\n]') "Set real local $name metadata before provisioning."
    }
    Assert-Lab ($env:LAB_PUBLISHER_EMAIL -match '^[^@\s]+@[^@\s]+\.[^@\s]+$') 'Publisher email is invalid.'
    $null = New-Item -ItemType Directory -Path $Context.Directory -Force
    $parameterPath = Join-Path $Context.Directory 'compiled-parameters.json'
    Invoke-LabCommand az @('bicep', 'build-params', '--file', (Join-Path $Context.Root 'infra\deploy.bicepparam'), '--outfile', $parameterPath) -Json $false
    $parameters = (Get-Content -LiteralPath $parameterPath -Raw | ConvertFrom-Json -AsHashtable).parameters
    Assert-Lab ($parameters.environmentName.value -ceq $Context.Target.environment -and $parameters.location.value -ceq 'westus3') 'Compiled parameter target mismatch.'
    foreach ($key in @('modelName', 'modelVersion', 'modelDeploymentName')) {
        Assert-Lab ($parameters[$key].value -cmatch '^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}$') "Invalid $key."
    }
    Assert-Lab ($parameters.modelName.value -match '^gpt-([5-9]|[1-9][0-9])(?:[.-]|$)') 'A GPT-5-family or newer model is required.'
    Assert-Lab ($parameters.modelDeploymentSku.value -ceq 'GlobalStandard') 'No deployment SKU substitution is permitted.'
    Assert-Lab ($parameters.modelCapacity.value -ge 1 -and $parameters.modelCapacity.value -le 20) 'Per-account capacity must be 1-20.'
    $hash = (Get-FileHash -LiteralPath $parameterPath -Algorithm SHA256).Hash
    if ($Context.State['parameterHash']) {
        Assert-Lab ($hash -eq $Context.State.parameterHash) 'Deployment inputs changed for this environment. Review changes explicitly; use a new environment rather than silently replacing resources.'
    }
    $Context.State.parameterHash = $hash
    return $parameters
}

function Assert-LabModel($Context, $Parameters, $Inventory) {
    $name = $Parameters.modelName.value
    $version = $Parameters.modelVersion.value
    $skuName = $Parameters.modelDeploymentSku.value
    $capacity = [int]$Parameters.modelCapacity.value
    $models = @(Invoke-LabCommand az @('cognitiveservices', 'model', 'list', '--subscription', $Context.Target.subscription, '--location', 'westus3'))
    $matches = @($models | Where-Object { $_.kind -eq 'AIServices' -and $_.model.name -ceq $name -and $_.model.version -ceq $version })
    Assert-Lab ($matches.Count -eq 1 -and $matches[0].model.capabilities.responses -eq 'true') 'Exact model/version with Responses support is not uniquely available for AIServices in West US 3.'
    Assert-Lab ($matches[0].model.lifecycleStatus -eq 'GenerallyAvailable') 'Model lifecycle is not GenerallyAvailable; revisit the design.'
    $skus = @($matches[0].model.skus | Where-Object { $_.name -ceq $skuName })
    Assert-Lab ($skus.Count -eq 1 -and $skus[0].usageName) 'Missing exact model SKU/quota metadata.'
    $sku = $skus[0]
    $bounds = $sku['capacity']
    Assert-Lab ($bounds -and $null -ne $bounds['maximum'] -and $bounds.maximum -ge $capacity) 'Requested capacity exceeds or is missing the catalog maximum.'
    if ($null -ne $bounds['minimum']) {
        Assert-Lab ($bounds.minimum -le $capacity) 'Requested capacity violates the catalog minimum.'
    }
    if ($null -ne $bounds['step']) {
        Assert-Lab ($bounds.step -gt 0 -and $null -ne $bounds['minimum']) 'Catalog step is invalid or lacks its minimum anchor.'
        Assert-Lab ((($capacity - $bounds.minimum) % $bounds.step) -eq 0) 'Requested capacity violates the catalog step.'
    }
    if ($null -eq $bounds['minimum'] -or $null -eq $bounds['step']) {
        Write-Host 'Catalog minimum/step is not fully specified; no values inferred. Provider validation remains required before deployment.'
    }
    if ($sku['deprecationDate']) { Assert-Lab ([DateTimeOffset]::Parse($sku.deprecationDate) -gt [DateTimeOffset]::UtcNow) 'Selected SKU is retired.' }
    if ($sku.capacity['allowedValues']) { Assert-Lab ($capacity -in $sku.capacity.allowedValues) 'Requested capacity is not an allowed catalog value.' }
    $delta = 2 * $capacity
    if ($Inventory) {
        foreach ($account in @($Inventory.resources | Where-Object type -eq 'Microsoft.CognitiveServices/accounts')) {
            $deployments = @(Invoke-LabCommand az @('cognitiveservices', 'account', 'deployment', 'list', '--subscription', $Context.Target.subscription, '--resource-group', $Context.Target.group, '--name', $account.name))
            Assert-Lab ($deployments.Count -le 1) 'Unexpected extra model deployment.'
            foreach ($deployment in $deployments) {
                Assert-Lab ($deployment.name -ceq $Parameters.modelDeploymentName.value -and $deployment.properties.model.name -ceq $name -and $deployment.properties.model.version -ceq $version -and $deployment.sku.name -ceq $skuName -and $deployment.sku.capacity -eq $capacity) 'Existing model configuration differs; refusing an implicit upgrade or capacity change.'
                $delta -= $capacity
            }
        }
    }
    $usage = @(Invoke-LabCommand az @('cognitiveservices', 'usage', 'list', '--subscription', $Context.Target.subscription, '--location', 'westus3'))
    $quota = @($usage | Where-Object { $_.name.value -ceq $sku.usageName })
    Assert-Lab ($quota.Count -eq 1 -and $null -ne $quota[0].limit -and $null -ne $quota[0].currentValue) 'Exact quota result is missing/ambiguous, not zero.'
    Assert-Lab (($quota[0].limit - $quota[0].currentValue) -ge $delta) "Insufficient model quota for $delta additional capacity units."
    $path = "/subscriptions/$($Context.Target.subscription)/providers/Microsoft.CognitiveServices/modelCapacities?api-version=2025-06-01&modelFormat=OpenAI&modelName=$name&modelVersion=$version"
    $available = @(Get-LabRestList $path | Where-Object { $_.location -eq 'westus3' -and $_.properties.skuName -ceq $skuName })
    Assert-Lab ($available.Count -eq 1 -and $null -ne $available[0].properties.availableCapacity) 'Exact regional capacity result is missing/ambiguous.'
    Assert-Lab ($available[0].properties.availableCapacity -ge $delta) 'Insufficient available regional capacity.'
    Write-Host "Model preflight: $name / $version / $skuName; $capacity per account; additional allocation $delta. Snapshot, not a reservation."
}

function Invoke-LabPreflight($Context, [switch]$CreateGroup) {
    Assert-Lab (-not $Context.State.retired) 'This environment was torn down; use a new environment/name because soft-deleted names may remain reserved.'
    $null = Assert-LabAccount $Context
    $tools = Invoke-LabCommand az @('version')
    Assert-Lab ([version]$tools['azure-cli'] -ge [version]'2.83.0') 'Azure CLI 2.83.0 or newer is required for the verified validation/parameter commands.'
    $parameters = Get-LabParameters $Context
    $inventory = Get-LabOwnedResources $Context -AllowMissing
    Assert-LabModel $Context $parameters $inventory
    foreach ($namespace in @('Microsoft.ApiManagement', 'Microsoft.CognitiveServices', 'Microsoft.OperationalInsights', 'Microsoft.Insights')) {
        $provider = Invoke-LabCommand az @('provider', 'show', '--subscription', $Context.Target.subscription, '--namespace', $namespace)
        Assert-Lab ($provider.registrationState -eq 'Registered') "$namespace is not registered; an authorized operator must register it separately."
        $type = switch ($namespace) {
            Microsoft.ApiManagement { 'service' }
            Microsoft.CognitiveServices { 'accounts' }
            Microsoft.OperationalInsights { 'workspaces' }
        }
        if ($type) {
            $resourceTypes = @($provider.resourceTypes | Where-Object resourceType -eq $type)
            Assert-Lab ($resourceTypes.Count -eq 1 -and @($resourceTypes[0].locations | Where-Object { ($_ -replace ' ', '') -eq 'westus3' }).Count -gt 0) "$namespace/$type is not available in West US 3."
        }
    }
    if (-not $inventory) {
        Assert-LabPermission $Context "/subscriptions/$($Context.Target.subscription)" @('Microsoft.Resources/subscriptions/resourceGroups/write')
        if (-not $CreateGroup) { Write-Host 'Preflight complete; no group created. Provider validation runs after authorized group creation.'; return }
        Save-LabState $Context
        $null = Invoke-LabCommand az @('group', 'create', '--subscription', $Context.Target.subscription, '--name', $Context.Target.group, '--location', 'westus3', '--tags', "azd-env-name=$($Context.Target.environment)", 'purpose=apim-responses-affinity-lab', "lab-owner=$($Context.State.owner)")
        $null = Get-LabOwnedResources $Context
    }
    Assert-LabPermission $Context (Get-LabGroupId $Context) @('Microsoft.Authorization/roleAssignments/write', 'Microsoft.Resources/deployments/write')
    $parametersFile = Join-Path $Context.Root 'infra\deploy.bicepparam'
    $common = @('--subscription', $Context.Target.subscription, '--resource-group', $Context.Target.group, '--parameters', $parametersFile)
    $null = Invoke-LabCommand az (@('deployment', 'group', 'validate', '--validation-level', 'Provider') + $common)
    Invoke-LabCommand az (@('deployment', 'group', 'what-if', '--validation-level', 'Provider') + $common) -Json $false
    Save-LabState $Context
}

function Export-LabRuntime($Context) {
    $null = Assert-LabAccount $Context
    $null = Get-LabOwnedResources $Context
    $deployments = @(Invoke-LabCommand az @('deployment', 'group', 'list', '--subscription', $Context.Target.subscription, '--resource-group', $Context.Target.group))
    $roots = @($deployments | Where-Object { $_.properties['outputs'] -and $_.properties.outputs['responsesEndpoint'] -and $_.properties.outputs['apimResourceId'] } | Sort-Object { [DateTimeOffset]::Parse($_.properties.timestamp) } -Descending)
    Assert-Lab ($roots.Count -gt 0 -and $roots[0].properties.provisioningState -eq 'Succeeded') 'Latest lab root deployment is missing or unsuccessful; refusing stale output export.'
    $incomplete = @($deployments | Where-Object {
        [DateTimeOffset]::Parse($_.properties.timestamp) -ge [DateTimeOffset]::Parse($roots[0].properties.timestamp) -and
        $_.properties.provisioningState -ne 'Succeeded'
    })
    Assert-Lab ($incomplete.Count -eq 0) 'A newer deployment is unsuccessful or incomplete; refusing stale output export.'
    $output = $roots[0].properties.outputs
    Assert-Lab ($output.resourceGroupName.value -eq $Context.Target.group) 'Deployment output group mismatch.'
    $mapping = @{
        LAB_RESPONSES_ENDPOINT = 'responsesEndpoint'
        LAB_MODEL_DEPLOYMENT = 'modelDeploymentName'
        LAB_AFFINITY_COOKIE_NAME = 'affinityCookieName'
        LAB_APIM_RESOURCE_ID = 'apimResourceId'
        LAB_APIM_SUBSCRIPTION_RESOURCE_ID = 'apimSubscriptionResourceId'
        LAB_BACKEND_A_ID = 'backendAId'
        LAB_BACKEND_B_ID = 'backendBId'
        LAB_FOUNDRY_A_RESOURCE_ID = 'foundryAResourceId'
        LAB_FOUNDRY_B_RESOURCE_ID = 'foundryBResourceId'
        LAB_WORKSPACE_RESOURCE_ID = 'workspaceResourceId'
        LAB_WORKSPACE_CUSTOMER_ID = 'workspaceCustomerId'
    }
    $runtime = @{}
    foreach ($key in $mapping.Keys) {
        $value = $output[$mapping[$key]].value
        Assert-Lab (-not [string]::IsNullOrWhiteSpace($value)) "Missing required deployment output $($mapping[$key])."
        if ($key.EndsWith('_ID') -and $key -ne 'LAB_WORKSPACE_CUSTOMER_ID') {
            Assert-Lab ($value.StartsWith((Get-LabGroupId $Context) + '/', [StringComparison]::OrdinalIgnoreCase)) "Output $key is outside the exact owned group."
        }
        $runtime[$key] = $value
    }
    $endpoint = [uri]$runtime.LAB_RESPONSES_ENDPOINT
    $gateway = [uri]$output.apimGatewayUrl.value
    Assert-Lab ($endpoint.Scheme -eq 'https' -and $endpoint.Authority -eq $gateway.Authority -and $endpoint.AbsolutePath -eq '/openai/v1/responses' -and -not $endpoint.Query) 'Unexpected Responses output URL.'
    Assert-Lab ($runtime.LAB_APIM_SUBSCRIPTION_RESOURCE_ID -eq "$($runtime.LAB_APIM_RESOURCE_ID)/subscriptions/lab-access") 'Subscription output is not the lab API-scoped access resource.'
    $Context.State.deployment = $roots[0].name
    $Context.State.outputs = $runtime
    Save-LabState $Context
    $runtime | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Context.Directory 'runtime.json') -Encoding utf8NoBOM
    Write-Host 'Non-secret runtime configuration exported; no subscription key retrieved.'
    return $runtime
}

function Assert-LabDeletion($Context, [string]$Confirmation) {
    Assert-Lab ($Confirmation -ceq $Context.Target.group) 'Deletion requires the exact group name as confirmation.'
    $null = Assert-LabAccount $Context
    $inventory = Get-LabOwnedResources $Context -AllowMissing
    if ($inventory) {
        Assert-LabPermission $Context (Get-LabGroupId $Context) @('Microsoft.Resources/subscriptions/resourceGroups/delete')
        foreach ($resource in $inventory.resources) { Write-Host "Delete inventory: $($resource.type) $($resource.id)" }
        Write-Host 'Deletion removes the entire owned group, including workspace evidence. Soft-delete records are not purged.'
    }
    return $inventory
}

function Complete-LabDeletion($Context) {
    $exists = Invoke-LabCommand az @('group', 'exists', '--subscription', $Context.Target.subscription, '--name', $Context.Target.group)
    Assert-Lab (-not $exists) 'Group still exists; teardown is not complete.'
    $Context.State.retired = $true
    $Context.State.Remove('outputs')
    Save-LabState $Context
    $runtimePath = Join-Path $Context.Directory 'runtime.json'
    if (Test-Path -LiteralPath $runtimePath) { Remove-Item -LiteralPath $runtimePath }
    Write-Host 'Owned group verified absent. Retained local ownership record; Azure soft-delete/name/quota retention may remain.'
}

function Get-LabTokenIdentity([string]$Token) {
    $parts = $Token.Split('.')
    Assert-Lab ($parts.Count -eq 3) 'Unexpected management token format.'
    $payload = $parts[1].Replace('-', '+').Replace('_', '/')
    $payload = $payload.PadRight($payload.Length + ((4 - $payload.Length % 4) % 4), '=')
    $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json -AsHashtable
    Assert-Lab ($claims.oid -and $claims.tid) 'Management token lacks identity claims.'
    "$($claims.tid)/$($claims.oid)"
}

function Initialize-LabAzd($Context, [switch]$RequireExisting) {
    Assert-Lab ($null -ne (Get-Command azd -ErrorAction SilentlyContinue)) 'Azure Developer CLI (azd) is required for this driver.'
    $account = Assert-LabAccount $Context
    $azdVersion = Invoke-LabCommand azd @('version', '--output', 'json')
    Assert-Lab ([version]($azdVersion.azd.version -replace '-.*$', '') -ge [version]'1.36.0') 'azd 1.36.0 or newer is required.'
    $cliToken = Invoke-LabCommand az @('account', 'get-access-token', '--subscription', $Context.Target.subscription, '--resource', 'https://management.azure.com/')
    $azdToken = Invoke-LabCommand azd @('auth', 'token', '--tenant-id', $account.tenantId, '--scope', 'https://management.azure.com/.default', '--output', 'json')
    Assert-Lab ((Get-LabTokenIdentity $cliToken.accessToken) -ceq (Get-LabTokenIdentity $azdToken.token)) 'azd and Azure CLI identities differ. Sign both in as the same operator; no login or subscription switching is automatic.'
    $envPath = Join-Path $Context.Root ".azure\$($Context.Target.environment)\.env"
    if (-not (Test-Path -LiteralPath $envPath)) {
        Assert-Lab (-not $RequireExisting) 'No azd environment. Use the CLI teardown path for this exact owned group.'
        Invoke-LabCommand azd @('env', 'new', $Context.Target.environment, '--subscription', $Context.Target.subscription, '--location', 'westus3', '--no-prompt') -Json $false
    } else {
        $existing = Invoke-LabCommand azd @('env', 'get-values', '--environment', $Context.Target.environment, '--output', 'json')
        foreach ($pair in @(@('AZURE_SUBSCRIPTION_ID', 'subscription'), @('AZURE_RESOURCE_GROUP', 'group'), @('AZURE_LOCATION', 'location'))) {
            Assert-Lab ($existing[$pair[0]] -ceq $Context.Target[$pair[1]]) 'Existing azd environment targets different resources; refusing to overwrite it.'
        }
        Assert-Lab ($existing['LAB_OWNER_ID'] -ceq $Context.State.owner -and $existing['AZURE_ENV_NAME'] -ceq $Context.Target.environment) 'Existing azd environment owner/name differs from the local ownership record.'
    }
    if ($RequireExisting) { return }
    $parameters = Get-LabParameters $Context
    $values = @{
        AZURE_SUBSCRIPTION_ID = $Context.Target.subscription
        AZURE_RESOURCE_GROUP = $Context.Target.group
        AZURE_LOCATION = $Context.Target.location
        LAB_OWNER_ID = $Context.State.owner
    }
    $mapping = @{
        LAB_PUBLISHER_NAME = 'publisherName'; LAB_PUBLISHER_EMAIL = 'publisherEmail'
        LAB_MODEL_NAME = 'modelName'; LAB_MODEL_VERSION = 'modelVersion'
        LAB_MODEL_DEPLOYMENT = 'modelDeploymentName'; LAB_MODEL_SKU = 'modelDeploymentSku'
        LAB_MODEL_CAPACITY = 'modelCapacity'
    }
    foreach ($key in $mapping.Keys) { $values[$key] = [string]$parameters[$mapping[$key]].value }
    foreach ($key in $values.Keys) {
        Invoke-LabCommand azd @('env', 'set', $key, $values[$key], '--environment', $Context.Target.environment) -Json $false
    }
}

function Invoke-LabProvision($Context, [ValidateSet('Azd', 'Cli')][string]$Driver) {
    if ($Driver -eq 'Azd') { Initialize-LabAzd $Context }
    Invoke-LabPreflight $Context -CreateGroup
    if ($Driver -eq 'Azd') {
        Invoke-LabCommand azd @('provision', '--environment', $Context.Target.environment, '--no-prompt') -Json $false
    } else {
        $null = Invoke-LabCommand az @('deployment', 'group', 'create', '--subscription', $Context.Target.subscription, '--resource-group', $Context.Target.group, '--name', "lab-$($Context.Target.environment)", '--parameters', (Join-Path $Context.Root 'infra\deploy.bicepparam'))
    }
    $null = Export-LabRuntime $Context
}

function Remove-LabDeployment($Context, [ValidateSet('Azd', 'Cli')][string]$Driver, [string]$Confirmation) {
    $inventory = Assert-LabDeletion $Context $Confirmation
    if ($inventory) {
        if ($Driver -eq 'Azd') {
            Initialize-LabAzd $Context -RequireExisting
            Invoke-LabCommand azd @('down', '--environment', $Context.Target.environment, '--force', '--purge=false', '--no-prompt') -Json $false
        } else {
            $null = Invoke-LabCommand az @('group', 'delete', '--subscription', $Context.Target.subscription, '--name', $Context.Target.group, '--yes')
        }
    }
    Complete-LabDeletion $Context
}

function Invoke-LabClient($Context, [string]$ClientPath, [string[]]$ClientArguments, [switch]$AllowFaults) {
    if ('--allow-faults' -in $ClientArguments -or 'failure' -in $ClientArguments) {
        Assert-Lab $AllowFaults 'Failure client requires the separate -AllowFaults opt-in.'
    }
    if ($AllowFaults) {
        Assert-Lab ($env:LAB_ALLOW_AZURE -ceq 'true' -and $env:LAB_CONFIRM_RESOURCE_GROUP -ceq $Context.Target.group) 'Failure client requires live authorization and exact group confirmation.'
        Assert-Lab (-not (Test-Path -LiteralPath (Join-Path $Context.Directory 'fault-recovery.json'))) 'An unresolved fault-recovery.json blocks another failure run.'
    }
    Assert-Lab (Test-Path -LiteralPath $ClientPath -PathType Leaf) 'Supply the published client executable path; see src\ResponsesLab\README.md.'
    Assert-Lab ($null -ne (Get-Command $ClientPath -CommandType Application -ErrorAction SilentlyContinue)) 'ClientPath must be an executable, not a shell script.'
    $runtime = Export-LabRuntime $Context
    if ($AllowFaults) {
        foreach ($member in @('A', 'B')) {
            Assert-Lab ($runtime["LAB_BACKEND_${member}_ID"] -ceq "$($runtime.LAB_APIM_RESOURCE_ID)/backends/backend-$($member.ToLowerInvariant())") 'Fault target is not an exact allow-listed backend.'
            Assert-LabPermission $Context $runtime["LAB_BACKEND_${member}_ID"] @('Microsoft.ApiManagement/service/backends/read', 'Microsoft.ApiManagement/service/backends/write')
        }
    }
    $access = Get-LabRest "$($runtime.LAB_APIM_SUBSCRIPTION_RESOURCE_ID)?api-version=2024-05-01"
    Assert-Lab ($access.properties.scope -eq "$($runtime.LAB_APIM_RESOURCE_ID)/apis/responses" -and $access.properties.state -eq 'active') 'Gateway subscription is not active and scoped to the Responses API.'
    $keys = Invoke-LabCommand az @('rest', '--method', 'post', '--url', "https://management.azure.com$($runtime.LAB_APIM_SUBSCRIPTION_RESOURCE_ID)/listSecrets?api-version=2024-05-01")
    Assert-Lab (-not [string]::IsNullOrWhiteSpace($keys.primaryKey)) 'No gateway subscription key returned.'
    $previous = @{}
    try {
        $runtime.LAB_ENABLE_FAULTS = $AllowFaults.ToString().ToLowerInvariant()
        $runtime.LAB_FAULT_RECOVERY_PATH = Join-Path $Context.Directory 'fault-recovery.json'
        $runtime.LAB_APIM_SUBSCRIPTION_KEY = $keys.primaryKey
        foreach ($key in $runtime.Keys) {
            $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
            [Environment]::SetEnvironmentVariable($key, $runtime[$key], 'Process')
        }
        & $ClientPath @ClientArguments
        Assert-Lab ($LASTEXITCODE -eq 0) 'Client returned a failing exit status.'
    } finally {
        Restore-LabProcessEnvironment $previous
        $runtime.Remove('LAB_APIM_SUBSCRIPTION_KEY')
        $runtime.Remove('LAB_ENABLE_FAULTS')
        $runtime.Remove('LAB_FAULT_RECOVERY_PATH')
        $keys = $null
    }
}

Export-ModuleMember -Function *-Lab*
