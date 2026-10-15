#requires -Version 7

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $root ('artifacts\004-tests-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot -Force
$module = Import-Module (Join-Path $PSScriptRoot 'Lab.psm1') -Force -PassThru
$oldEnvironment = @{}
$synthetic = @{
    AZURE_ENV_NAME = 'offline'
    AZURE_LOCATION = 'westus3'
    LAB_PUBLISHER_NAME = 'OFFLINE_TEST'
    LAB_PUBLISHER_EMAIL = 'offline@example.org'
    LAB_OWNER_ID = '22222222-2222-4222-8222-222222222222'
    LAB_MODEL_NAME = 'gpt-5.4-mini'
    LAB_MODEL_VERSION = '2026-03-17'
    LAB_MODEL_DEPLOYMENT = 'responses-chat'
    LAB_MODEL_SKU = 'GlobalStandard'
    LAB_MODEL_CAPACITY = '20'
    LAB_ALLOW_AZURE = 'false'
}
foreach ($key in $synthetic.Keys) {
    $oldEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $synthetic[$key], 'Process')
}

function Test-Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Test-Fails([scriptblock]$Operation, [string]$Pattern) {
    $failed = $false
    try { & $Operation }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $failed = $true
    }
    Test-Assert $failed "Expected failure matching $Pattern"
}
$script:passed = 0
function Test-Case([string]$Name, [scriptblock]$Operation) {
    & $module { Reset-Fixture }
    & $Operation
    $script:passed++
    Write-Output "PASS $Name"
}

try {
    # This is the only real az call: local native-parameter compilation, no sign-in.
    $compiled = Join-Path $testRoot 'parameters.json'
    & az bicep build-params --file (Join-Path $root 'infra\deploy.bicepparam') --outfile $compiled --only-show-errors
    Test-Assert ($LASTEXITCODE -eq 0) 'Offline parameter compilation failed'
    & $module {
        param($TestRoot, $Compiled)
        $script:TestRoot = $TestRoot
        $script:Compiled = Get-Content -LiteralPath $Compiled -Raw
        $script:Sequence = 0
        function script:Reset-Fixture {
            $script:Sequence++
            $contextRoot = Join-Path $script:TestRoot "case-$script:Sequence"
            $script:Context = Get-LabContext $contextRoot offline '11111111-1111-4111-8111-111111111111' rg-responses-offline
            $script:Context.State.owner = '22222222-2222-4222-8222-222222222222'
            $script:Calls = [Collections.Generic.List[object]]::new()
            $id = Get-LabGroupId $script:Context
            $tags = @{ 'lab-owner' = $script:Context.State.owner; 'azd-env-name' = 'offline'; purpose = 'apim-responses-affinity-lab' }
            $script:Fixture = @{
                exists = $false
                account = @{ id = $script:Context.Target.subscription; name = 'Offline subscription'; environmentName = 'AzureCloud'; state = 'Enabled'; tenantId = '33333333-3333-4333-8333-333333333333' }
                group = @{ id = $id; location = 'westus3'; tags = $tags }
                resources = @()
                permissions = @{ value = @(@{ actions = @('*'); notActions = @() }) }
                models = @(@{ kind = 'AIServices'; model = @{
                    name = 'gpt-5.4-mini'; version = '2026-03-17'; capabilities = @{ responses = 'true' }; lifecycleStatus = 'GenerallyAvailable'
                    skus = @(@{ name = 'GlobalStandard'; usageName = 'OpenAI.GlobalStandard.gpt-5.4-mini'; capacity = @{ minimum = 1; maximum = 1000; step = 1 } })
                } })
                usage = @(@{ name = @{ value = 'OpenAI.GlobalStandard.gpt-5.4-mini' }; currentValue = 0; limit = 100 })
                capacity = @{ value = @(@{ location = 'westus3'; properties = @{ skuName = 'GlobalStandard'; availableCapacity = 100 } }) }
                deployments = @()
                modelDeployments = @()
                providerRegistered = $true
                providerRegion = 'West US 3'
                failValidation = $false
                secret = 'synthetic-key-not-real'
                token = 'e30.' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"tid":"33333333-3333-4333-8333-333333333333","oid":"66666666-6666-4666-8666-666666666666"}')).TrimEnd('=').Replace('+', '-').Replace('/', '_') + '.offline'
                tokenMismatch = $false
                azdEnvironment = @{
                    AZURE_ENV_NAME = 'offline'; AZURE_SUBSCRIPTION_ID = $script:Context.Target.subscription
                    AZURE_RESOURCE_GROUP = $script:Context.Target.group; AZURE_LOCATION = 'westus3'
                    LAB_OWNER_ID = $script:Context.State.owner; LAB_MODEL_NAME = 'stale-model'
                }
            }
            $script:Outputs = @{
                resourceGroupName = @{ value = 'rg-responses-offline' }
                responsesEndpoint = @{ value = 'https://offline.example.invalid/openai/v1/responses' }
                apimGatewayUrl = @{ value = 'https://offline.example.invalid' }
                modelDeploymentName = @{ value = 'responses-chat' }
                affinityCookieName = @{ value = 'APIM-Backend-Affinity' }
                apimResourceId = @{ value = "$id/providers/Microsoft.ApiManagement/service/gateway" }
                apimSubscriptionResourceId = @{ value = "$id/providers/Microsoft.ApiManagement/service/gateway/subscriptions/lab-access" }
                backendAId = @{ value = "$id/providers/Microsoft.ApiManagement/service/gateway/backends/backend-a" }
                backendBId = @{ value = "$id/providers/Microsoft.ApiManagement/service/gateway/backends/backend-b" }
                foundryAResourceId = @{ value = "$id/providers/Microsoft.CognitiveServices/accounts/ai-a" }
                foundryBResourceId = @{ value = "$id/providers/Microsoft.CognitiveServices/accounts/ai-b" }
                workspaceResourceId = @{ value = "$id/providers/Microsoft.OperationalInsights/workspaces/logs" }
                workspaceCustomerId = @{ value = '44444444-4444-4444-8444-444444444444' }
            }
        }
        function script:Set-OwnedFixture {
            $script:Fixture.exists = $true
            $script:Fixture.resources = @(
                @{ id = $script:Outputs.apimResourceId.value; type = 'Microsoft.ApiManagement/service'; name = 'gateway'; tags = $script:Fixture.group.tags },
                @{ id = $script:Outputs.foundryAResourceId.value; type = 'Microsoft.CognitiveServices/accounts'; name = 'ai-a'; tags = $script:Fixture.group.tags },
                @{ id = $script:Outputs.foundryBResourceId.value; type = 'Microsoft.CognitiveServices/accounts'; name = 'ai-b'; tags = $script:Fixture.group.tags },
                @{ id = $script:Outputs.workspaceResourceId.value; type = 'Microsoft.OperationalInsights/workspaces'; name = 'logs'; tags = $script:Fixture.group.tags }
            )
            $script:Fixture.deployments = @(@{ name = 'lab-offline'; properties = @{ outputs = $script:Outputs; timestamp = '2026-10-15T00:00:00Z'; provisioningState = 'Succeeded' } })
        }
        function script:Invoke-LabCommand {
            param([string]$Tool, [string[]]$Arguments, [bool]$Json = $true)
            $script:Calls.Add(@{ Tool = $Tool; Arguments = $Arguments; Json = $Json })
            $command = "$Tool $($Arguments -join ' ')"
            switch -Regex ($command) {
                '^azd version ' { return @{ azd = @{ version = '1.36.0' } } }
                '^azd auth token ' {
                    if ($script:Fixture.tokenMismatch) { return @{ token = 'e30.eyJ0aWQiOiJvdGhlciIsIm9pZCI6Im90aGVyIn0.offline' } }
                    return @{ token = $script:Fixture.token }
                }
                '^azd env new ' {
                    $directory = Join-Path $script:Context.Root '.azure\offline'
                    $null = New-Item -ItemType Directory -Path $directory -Force
                    Set-Content -LiteralPath (Join-Path $directory '.env') -Value 'SYNTHETIC=true'
                    return
                }
                '^azd env get-values ' { return $script:Fixture.azdEnvironment }
                '^azd env set ' { $script:Fixture.azdEnvironment[$Arguments[2]] = $Arguments[3]; return }
                '^azd provision ' { Set-OwnedFixture; return }
                '^azd down ' { $script:Fixture.exists = $false; return }
                '^az account show$' { return $script:Fixture.account }
                '^az account get-access-token ' { return @{ accessToken = $script:Fixture.token } }
                '^az version$' { return @{ 'azure-cli' = '2.83.0' } }
                '^az bicep build-params ' { [IO.File]::WriteAllText($Arguments[5], $script:Compiled); return }
                '^az group exists ' { return $script:Fixture.exists }
                '^az group show ' { return $script:Fixture.group }
                '^az group create ' { $script:Fixture.exists = $true; return $script:Fixture.group }
                '^az group delete ' { $script:Fixture.exists = $false; return }
                '^az resource list ' { return $script:Fixture.resources }
                '^az cognitiveservices model list ' { return $script:Fixture.models }
                '^az cognitiveservices usage list ' { return $script:Fixture.usage }
                '^az cognitiveservices account deployment list ' { return $script:Fixture.modelDeployments }
                '^az rest .*modelCapacities' { return $script:Fixture.capacity }
                '^az rest .*Microsoft.Authorization/permissions' { return $script:Fixture.permissions }
                '^az rest .*listSecrets' { return @{ primaryKey = $script:Fixture.secret } }
                '^az rest ' { return @{ properties = @{ scope = "$($script:Outputs.apimResourceId.value)/apis/responses"; state = 'active' } } }
                '^az provider show ' {
                    $type = switch ($Arguments[-1]) {
                        Microsoft.ApiManagement { 'service' }
                        Microsoft.CognitiveServices { 'accounts' }
                        Microsoft.OperationalInsights { 'workspaces' }
                        Microsoft.Insights { 'diagnosticSettings' }
                    }
                    return @{ registrationState = $(if ($script:Fixture.providerRegistered) { 'Registered' } else { 'NotRegistered' }); resourceTypes = @(@{ resourceType = $type; locations = @($script:Fixture.providerRegion) }) }
                }
                '^az deployment group validate ' { if ($script:Fixture.failValidation) { throw 'Synthetic provider validation denied' }; return @{ properties = @{} } }
                '^az deployment group what-if ' { return }
                '^az deployment group list ' { return $script:Fixture.deployments }
                '^az deployment group create ' { Set-OwnedFixture; return @{} }
                default { throw "Unexpected external invocation in offline test: $command" }
            }
        }
    } $testRoot $compiled

    Test-Case 'read-only preflight never creates a group' {
        & $module { Invoke-LabPreflight $script:Context }
        Test-Assert (-not (& $module { $script:Fixture.exists })) 'Preflight created a group'
    }
    Test-Case 'authorized creation precedes Provider validation and what-if' {
        & $module { Invoke-LabPreflight $script:Context -CreateGroup }
        $calls = & $module { $script:Calls | ForEach-Object { $_.Arguments -join ' ' } }
        Test-Assert (($calls -join "`n") -match 'group create .*lab-owner=') 'Ownership marker missing from creation'
        Test-Assert (($calls -join "`n") -match 'validate --validation-level Provider') 'Full provider/RBAC validation missing'
        Test-Assert (($calls -join "`n") -match 'what-if --validation-level Provider') 'What-if missing'
    }
    Test-Case 'active subscription mismatch fails before writes' {
        & $module { $script:Fixture.account.id = '55555555-5555-4555-8555-555555555555' }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context -CreateGroup } } 'Active Azure CLI subscription differs'
        Test-Assert (-not (& $module { $script:Fixture.exists })) 'Mismatch caused a write'
    }
    Test-Case 'saved target cannot be changed' {
        & $module { Save-LabState $script:Context }
        Test-Fails { & $module { Get-LabContext $script:Context.Root offline '55555555-5555-4555-8555-555555555555' rg-responses-offline } } 'target changed'
    }
    Test-Case 'changed compiled inputs cannot silently replace resources' {
        & $module { $script:Context.State.parameterHash = 'different-prior-input-hash' }
        Test-Fails { & $module { Get-LabParameters $script:Context } } 'Deployment inputs changed'
    }
    Test-Case 'foreign group cannot be adopted even if empty' {
        & $module { $script:Fixture.exists = $true; $script:Fixture.group.tags['lab-owner'] = 'foreign' }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context -CreateGroup } } 'not owned'
    }
    Test-Case 'an untagged parent blocks reuse and teardown' {
        & $module { Set-OwnedFixture; $script:Fixture.resources[0].tags = @{} }
        Test-Fails { & $module { Get-LabOwnedResources $script:Context } } 'lacks this environment ownership'
    }
    Test-Case 'unknown resources block deletion' {
        & $module { Set-OwnedFixture; $script:Fixture.resources += @{ type = 'Microsoft.Sql/servers'; id = 'foreign' } }
        Test-Fails { & $module { Assert-LabDeletion $script:Context rg-responses-offline } } 'Unexpected resource type'
    }
    Test-Case 'exact deletion confirmation is mandatory before reads' {
        Test-Fails { & $module { Assert-LabDeletion $script:Context other-group } } 'exact group name'
        Test-Assert ((& $module { $script:Calls.Count }) -eq 0) 'Read occurred before confirmation'
    }
    Test-Case 'quota and empty capacity are not success-shaped defaults' {
        & $module { $script:Fixture.usage[0].limit = 39 }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context -CreateGroup } } 'Insufficient model quota'
        & $module { $script:Fixture.usage[0].limit = 100; $script:Fixture.capacity.value = @() }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context -CreateGroup } } 'capacity result is missing'
    }
    Test-Case 'exact catalog version and Responses support are required' {
        & $module { $script:Fixture.models[0].model.capabilities.responses = 'false' }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context } } 'Responses support'
    }
    Test-Case 'nullable catalog minimum and step still require Provider validation' {
        & $module {
            $script:Fixture.models[0].model.skus[0].capacity = @{ minimum = $null; step = $null; maximum = 10000000; default = 10 }
            Invoke-LabPreflight $script:Context -CreateGroup
        }
        $calls = & $module { $script:Calls | ForEach-Object { $_.Arguments -join ' ' } }
        Test-Assert (($calls -join "`n") -match 'validate --validation-level Provider') 'Nullable bounds skipped Provider validation'
        Test-Assert (($calls -join "`n") -match 'what-if --validation-level Provider') 'Nullable bounds skipped what-if'
    }
    Test-Case 'missing catalog maximum fails before group creation' {
        & $module { $script:Fixture.models[0].model.skus[0].capacity = @{ minimum = $null; step = $null; maximum = $null } }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context -CreateGroup } } 'catalog maximum'
        Test-Assert (-not (& $module { $script:Fixture.exists })) 'Missing maximum caused a write'
    }
    Test-Case 'declared catalog minimum step and allowed values remain enforced' {
        & $module { $script:Fixture.models[0].model.skus[0].capacity.minimum = 21 }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context } } 'catalog minimum'
        & $module { $script:Fixture.models[0].model.skus[0].capacity.minimum = 1; $script:Fixture.models[0].model.skus[0].capacity.step = 2 }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context } } 'catalog step'
        & $module { $script:Fixture.models[0].model.skus[0].capacity.step = 0 }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context } } 'Catalog step is invalid'
        & $module { $script:Fixture.models[0].model.skus[0].capacity.step = 1; $script:Fixture.models[0].model.skus[0].capacity.allowedValues = @(10, 30) }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context } } 'allowed catalog value'
    }
    Test-Case 'provider registration and region cannot silently fall back' {
        & $module { $script:Fixture.providerRegistered = $false }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context } } 'not registered'
        & $module { $script:Fixture.providerRegistered = $true; $script:Fixture.providerRegion = 'East US' }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context } } 'not available in West US 3'
    }
    Test-Case 'NotActions exclusions fail the effective permission check' {
        & $module { $script:Fixture.permissions.value[0].notActions = @('Microsoft.Resources/subscriptions/resourceGroups/write') }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context -CreateGroup } } 'No effective allow'
        Test-Assert (-not (& $module { $script:Fixture.exists })) 'Missing permission created a group'
    }
    Test-Case 'unchanged reruns do not demand a second quota allocation' {
        & $module {
            Set-OwnedFixture
            $script:Fixture.usage[0].currentValue = 100
            $script:Fixture.capacity.value[0].properties.availableCapacity = 0
            $script:Fixture.modelDeployments = @(@{ name = 'responses-chat'; sku = @{ name = 'GlobalStandard'; capacity = 20 }; properties = @{ model = @{ name = 'gpt-5.4-mini'; version = '2026-03-17' } } })
            Invoke-LabPreflight $script:Context -CreateGroup
        }
    }
    Test-Case 'provider failure is surfaced without a deployment' {
        & $module { $script:Fixture.failValidation = $true }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context -CreateGroup } } 'Synthetic provider validation denied'
        Test-Assert (-not ((& $module { $script:Calls | ForEach-Object { $_.Arguments -join ' ' } }) -match 'deployment group create')) 'Failure caused deployment'
    }
    Test-Case 'runtime export is allow-listed and validates target identifiers' {
        $runtime = & $module { Set-OwnedFixture; Export-LabRuntime $script:Context }
        Test-Assert ($runtime.Count -eq 11 -and -not $runtime.ContainsKey('LAB_APIM_SUBSCRIPTION_KEY')) 'Unsafe runtime export'
        & $module { $script:Outputs.backendAId.value = '/subscriptions/foreign/resourceGroups/other/providers/foo/bar' }
        Test-Fails { & $module { Export-LabRuntime $script:Context } } 'outside the exact owned group'
    }
    Test-Case 'new failed deployment refuses stale success outputs' {
        & $module { Set-OwnedFixture; $script:Fixture.deployments[0].properties.provisioningState = 'Failed' }
        Test-Fails { & $module { Export-LabRuntime $script:Context } } 'missing or unsuccessful'
    }
    Test-Case 'failed deployment without outputs cannot resurrect older configuration' {
        & $module {
            Set-OwnedFixture
            $script:Fixture.deployments += @{ name = 'new-root'; properties = @{ timestamp = '2026-10-15T00:01:00Z'; provisioningState = 'Failed' } }
        }
        Test-Fails { & $module { Export-LabRuntime $script:Context } } 'newer deployment is unsuccessful'
    }
    Test-Case 'pagination cannot cross subscription scope' {
        & $module { $script:Fixture.capacity.nextLink = 'https://management.azure.com/subscriptions/55555555-5555-4555-8555-555555555555/providers/Microsoft.CognitiveServices/modelCapacities?api-version=2025-06-01' }
        Test-Fails { & $module { Get-LabRestList "/subscriptions/$($script:Context.Target.subscription)/providers/Microsoft.CognitiveServices/modelCapacities?api-version=2025-06-01" } } 'Unexpected management pagination'
        Test-Assert ((& $module { $script:Calls.Count }) -eq 1) 'Cross-subscription follow-up was sent'
    }
    Test-Case 'azd initialization binds identity and synchronizes exact compiled inputs' {
        & $module {
            $previous = Set-LabProcessEnvironment $script:Context
            try {
                Initialize-LabAzd $script:Context
                Assert-Lab ($script:Fixture.azdEnvironment.LAB_MODEL_NAME -ceq 'gpt-5.4-mini') 'Stale azd model was retained'
                Assert-Lab ($script:Fixture.azdEnvironment.LAB_MODEL_CAPACITY -ceq '20') 'Capacity was not synchronized'
                Assert-Lab (-not $script:Fixture.azdEnvironment.ContainsKey('LAB_APIM_SUBSCRIPTION_KEY')) 'Key was saved to azd'
                Assert-Lab (@($script:Calls | Where-Object { $_.Tool -eq 'azd' -and ($_.Arguments -join ' ') -match '^auth token .*--output json$' }).Count -eq 1) 'Default plain-text azd token output was requested instead of JSON'
                Initialize-LabAzd $script:Context -RequireExisting
            } finally { Restore-LabProcessEnvironment $previous }
        }
    }
    Test-Case 'azd identity mismatch blocks local environment writes' {
        & $module { $script:Fixture.tokenMismatch = $true }
        Test-Fails { & $module { Initialize-LabAzd $script:Context } } 'identities differ'
        Test-Assert (-not ((& $module { $script:Calls | ForEach-Object { $_.Arguments -join ' ' } }) -match '^env (new|set) ')) 'Mismatched identity changed azd state'
    }
    Test-Case 'azd teardown refuses changed target and owner without rewriting either' {
        & $module {
            $directory = Join-Path $script:Context.Root '.azure\offline'
            $null = New-Item -ItemType Directory -Path $directory -Force
            Set-Content -LiteralPath (Join-Path $directory '.env') -Value 'SYNTHETIC=true'
            $script:Fixture.azdEnvironment.AZURE_RESOURCE_GROUP = 'other-group'
        }
        Test-Fails { & $module { Initialize-LabAzd $script:Context -RequireExisting } } 'targets different resources'
        & $module { $script:Fixture.azdEnvironment.AZURE_RESOURCE_GROUP = $script:Context.Target.group; $script:Fixture.azdEnvironment.LAB_OWNER_ID = 'other-owner' }
        Test-Fails { & $module { Initialize-LabAzd $script:Context -RequireExisting } } 'owner/name differs'
        Test-Assert (-not ((& $module { $script:Calls | ForEach-Object { $_.Arguments -join ' ' } }) -match '^env set ')) 'Unsafe teardown rewrote the environment'
    }
    Test-Case 'both provision drivers create and validate the owned group before deployment' {
        foreach ($driver in @('Azd', 'Cli')) {
            & $module {
                param($Driver)
                Reset-Fixture
                $previous = Set-LabProcessEnvironment $script:Context
                try { Invoke-LabProvision $script:Context $Driver }
                finally { Restore-LabProcessEnvironment $previous }
            } $driver
            $calls = @(& $module { $script:Calls | ForEach-Object { "$($_.Tool) $($_.Arguments -join ' ')" } })
            $createIndex = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -match '^az group create ' })
            $validateIndex = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -match '^az deployment group validate ' })
            $deployIndex = [array]::FindIndex($calls, [Predicate[string]] { param($c) $c -match '^azd provision |^az deployment group create ' })
            Test-Assert ($createIndex -ge 0 -and $validateIndex -gt $createIndex -and $deployIndex -gt $validateIndex) "$driver deployment bypassed preflight ordering"
            Test-Assert ((& $module { Test-Path -LiteralPath (Join-Path $script:Context.Directory 'runtime.json') })) "$driver did not export runtime configuration"
        }
    }
    Test-Case 'both teardown drivers verify absence and never authorize purge' {
        foreach ($driver in @('Azd', 'Cli')) {
            & $module {
                param($Driver)
                Reset-Fixture
                Set-OwnedFixture
                $directory = Join-Path $script:Context.Root '.azure\offline'
                $null = New-Item -ItemType Directory -Path $directory -Force
                Set-Content -LiteralPath (Join-Path $directory '.env') -Value 'SYNTHETIC=true'
                Remove-LabDeployment $script:Context $Driver rg-responses-offline
                Assert-Lab $script:Context.State.retired 'Deletion did not retire state'
                if ($Driver -eq 'Azd') {
                    $down = @($script:Calls | Where-Object { $_.Tool -eq 'azd' -and $_.Arguments[0] -eq 'down' })
                    Assert-Lab ($down.Count -eq 1 -and '--purge=false' -in $down[0].Arguments -and '--no-prompt' -in $down[0].Arguments) 'Teardown could offer a purge prompt'
                }
            } $driver
        }
    }
    Test-Case 'client failure restores process key without writing it' {
        & $module { Set-OwnedFixture }
        $oldKey = $env:LAB_APIM_SUBSCRIPTION_KEY
        $env:LAB_APIM_SUBSCRIPTION_KEY = 'prior-synthetic-key'
        try {
            $client = (Get-Command pwsh -CommandType Application).Source
            Test-Fails { & $module { param($Client) Invoke-LabClient $script:Context $Client @('-NoProfile', '-Command', 'if ($env:LAB_APIM_SUBSCRIPTION_KEY -ne "synthetic-key-not-real") { exit 8 }; exit 9') } $client } 'Client returned a failing'
            Test-Assert ($env:LAB_APIM_SUBSCRIPTION_KEY -ceq 'prior-synthetic-key') 'Process key was not restored'
            $text = & $module { Get-Content -LiteralPath $script:Context.StatePath -Raw }
            Test-Assert ($text -notmatch 'synthetic-key-not-real|LAB_APIM_SUBSCRIPTION_KEY') 'Secret was persisted'
        } finally { $env:LAB_APIM_SUBSCRIPTION_KEY = $oldKey }
    }
    Test-Case 'client failure removes a key that was absent before invocation' {
        & $module { Set-OwnedFixture }
        $oldKey = $env:LAB_APIM_SUBSCRIPTION_KEY
        Remove-Item Env:LAB_APIM_SUBSCRIPTION_KEY -ErrorAction SilentlyContinue
        try {
            $client = (Get-Command pwsh -CommandType Application).Source
            Test-Fails { & $module { param($Client) Invoke-LabClient $script:Context $Client @('-NoProfile', '-Command', 'exit 9') } $client } 'Client returned a failing'
            Test-Assert (-not (Test-Path Env:LAB_APIM_SUBSCRIPTION_KEY)) 'Previously absent key was left behind'
        } finally { $env:LAB_APIM_SUBSCRIPTION_KEY = $oldKey }
    }
    Test-Case 'teardown requires actual absence and retires environment state' {
        & $module { $script:Fixture.exists = $true }
        Test-Fails { & $module { Complete-LabDeletion $script:Context } } 'still exists'
        & $module { $script:Fixture.exists = $false; Complete-LabDeletion $script:Context }
        Test-Fails { & $module { Invoke-LabPreflight $script:Context } } 'was torn down'
    }
    Test-Case 'public entry refuses absent opt-ins without external calls' {
        $entry = Join-Path $PSScriptRoot 'Invoke-Lab.ps1'
        Test-Fails { & $entry -Action Preflight -EnvironmentName offline -SubscriptionId '11111111-1111-4111-8111-111111111111' } 'require -AllowAzure'
        Test-Fails { & $entry -Action Provision -EnvironmentName offline -SubscriptionId '11111111-1111-4111-8111-111111111111' -AllowAzure } 'requires -AllowDeploy'
        Test-Fails { & $entry -Action Down -EnvironmentName offline -SubscriptionId '11111111-1111-4111-8111-111111111111' -AllowAzure } 'requires -AllowDelete'
        Test-Fails { & $entry -Action Run -EnvironmentName offline -SubscriptionId '11111111-1111-4111-8111-111111111111' -AllowAzure } 'requires -AllowCredentialRead'
        Test-Fails { & $entry -Action Run -EnvironmentName offline -SubscriptionId '11111111-1111-4111-8111-111111111111' -AllowAzure -AllowCredentialRead -ClientArguments @('--allow-requests', '--scenario', 'failure') } 'requires -AllowFaults'
        Test-Fails { & $entry -Action Run -EnvironmentName offline -SubscriptionId '11111111-1111-4111-8111-111111111111' -AllowAzure -AllowCredentialRead -AllowFaults -ConfirmResourceGroup other-group } 'requires -AllowFaults'
        Test-Fails { & $entry -Action Run -EnvironmentName offline -SubscriptionId '11111111-1111-4111-8111-111111111111' -AllowAzure -AllowCredentialRead -AllowFaults -ConfirmResourceGroup rg-responses-offline -ClientArguments @('--allow-requests') } 'exact client argument sequence'
        Test-Fails { & (Join-Path $PSScriptRoot 'Azd-Hook.ps1') -Stage PreProvision } 'bare azd commands are not authorized'
    }
    Test-Case 'failure helper refuses missing authorization before reads or credential retrieval' {
        Test-Fails { & $module { Invoke-LabClient $script:Context missing @('--allow-requests', '--scenario', 'failure') } } 'separate -AllowFaults'
        Test-Fails { & $module { Invoke-LabClient $script:Context missing @() -AllowFaults } } 'live authorization and exact group confirmation'
        Test-Assert ((& $module { $script:Calls.Count }) -eq 0) 'Failure helper read Azure before authorization'
    }
    Test-Case 'failure handoff restores opt-in journal path and credentials on client failure' {
        $previous = @{}
        foreach ($key in @('LAB_ALLOW_AZURE', 'LAB_CONFIRM_RESOURCE_GROUP', 'LAB_ENABLE_FAULTS', 'LAB_FAULT_RECOVERY_PATH')) {
            $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        }
        try {
            $env:LAB_ALLOW_AZURE = 'true'
            $env:LAB_CONFIRM_RESOURCE_GROUP = 'rg-responses-offline'
            $env:LAB_ENABLE_FAULTS = 'prior-fault-setting'
            $env:LAB_FAULT_RECOVERY_PATH = 'prior-journal-setting'
            & $module { Set-OwnedFixture }
            $client = (Get-Command pwsh -CommandType Application).Source
            Test-Fails { & $module {
                param($Client)
                Invoke-LabClient $script:Context $Client @('-NoProfile', '-Command',
                    'if ($env:LAB_ENABLE_FAULTS -ne "true" -or -not $env:LAB_FAULT_RECOVERY_PATH.EndsWith("fault-recovery.json") -or $env:LAB_APIM_SUBSCRIPTION_KEY -ne "synthetic-key-not-real") { exit 8 }; exit 9') -AllowFaults
            } $client } 'Client returned a failing'
            Test-Assert ($env:LAB_ENABLE_FAULTS -ceq 'prior-fault-setting' -and $env:LAB_FAULT_RECOVERY_PATH -ceq 'prior-journal-setting') 'Fault handoff environment was not restored'
            $permissionCalls = @(& $module { $script:Calls | Where-Object { ($_.Arguments -join ' ') -match '/backends/backend-[ab]/providers/Microsoft.Authorization/permissions' } })
            Test-Assert ($permissionCalls.Count -eq 2) 'Both exact backend permissions were not checked'
        } finally {
            Restore-LabProcessEnvironment $previous
        }
    }
    Write-Output "Passed $script:passed offline lifecycle cases. No live Azure operation or real credential retrieval was performed."
} finally {
    foreach ($key in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $oldEnvironment[$key], 'Process') }
    Remove-Module $module.Name -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
