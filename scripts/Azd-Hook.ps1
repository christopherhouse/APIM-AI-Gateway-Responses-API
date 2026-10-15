#requires -Version 7

param([Parameter(Mandatory)][ValidateSet('PreProvision', 'PostProvision', 'PreDown', 'PostDown')][string]$Stage)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.psm1') -Force
Assert-Lab ($env:LAB_ALLOW_AZURE -ceq 'true') 'Use Invoke-Lab.ps1 with explicit action/target opt-ins; bare azd commands are not authorized.'
$context = Get-LabContext (Split-Path $PSScriptRoot -Parent) $env:AZURE_ENV_NAME $env:AZURE_SUBSCRIPTION_ID $env:AZURE_RESOURCE_GROUP -RequireExisting
Assert-Lab ($env:LAB_OWNER_ID -ceq $context.State.owner -and $env:AZURE_LOCATION -ceq 'westus3') 'Hook environment does not match the saved owner/region.'
switch ($Stage) {
    PreProvision {
        Assert-Lab ($env:LAB_ALLOW_DEPLOY -ceq 'true') 'Provisioning hook requires a deployment opt-in.'
        $null = Get-LabOwnedResources $context
        Invoke-LabPreflight $context
    }
    PostProvision { $null = Export-LabRuntime $context }
    PreDown {
        Assert-Lab ($env:LAB_ALLOW_DELETE -ceq 'true') 'Deletion hook requires a separate deletion opt-in.'
        $null = Assert-LabDeletion $context $env:LAB_CONFIRM_RESOURCE_GROUP
    }
    PostDown { Complete-LabDeletion $context }
}
