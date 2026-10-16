#requires -Version 7

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Preflight', 'Provision', 'Export', 'Run', 'Down')][string]$Action,
    [ValidateSet('Cli', 'Azd')][string]$Driver = 'Azd',
    [Parameter(Mandatory)][string]$EnvironmentName,
    [Parameter(Mandatory)][string]$SubscriptionId,
    [string]$ResourceGroupName = "rg-responses-$EnvironmentName",
    [switch]$AllowAzure,
    [switch]$AllowDeploy,
    [switch]$AllowAffinityChange,
    [switch]$AllowDelete,
    [switch]$AllowCredentialRead,
    [switch]$AllowFaults,
    [string]$ConfirmResourceGroup,
    [string]$ClientPath,
    [string[]]$ClientArguments = @()
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Lab.psm1') -Force
Assert-Lab $AllowAzure 'All live operations, including preflight reads, require -AllowAzure.'
if ($Action -eq 'Provision') { Assert-Lab $AllowDeploy 'Provision requires -AllowDeploy.' }
if ($AllowAffinityChange) {
    Assert-Lab ($Action -eq 'Provision' -and $AllowDeploy) 'Affinity changes require Provision -AllowDeploy -AllowAffinityChange.'
}
if ($Action -eq 'Down') {
    Assert-Lab ($AllowDelete -and $ConfirmResourceGroup -ceq $ResourceGroupName) 'Down requires -AllowDelete and the exact -ConfirmResourceGroup.'
}
if ($Action -eq 'Run') { Assert-Lab $AllowCredentialRead 'Run requires -AllowCredentialRead for management-plane key access.' }
if ($AllowFaults -or '--allow-faults' -in $ClientArguments -or 'failure' -in $ClientArguments) {
    Assert-Lab ($Action -eq 'Run' -and $AllowFaults -and $ConfirmResourceGroup -ceq $ResourceGroupName) 'Failure run requires -AllowFaults and the exact -ConfirmResourceGroup.'
    Assert-Lab (($ClientArguments -join '|') -ceq "--allow-requests|--scenario|failure|--allow-faults|--confirm-resource-group|$ResourceGroupName") 'Failure run requires the documented exact client argument sequence.'
}
Assert-Lab ($null -ne (Get-Command az -ErrorAction SilentlyContinue)) 'Azure CLI is required.'
Assert-Lab ($env:AZURE_LOCATION -in @($null, '', 'westus3')) 'AZURE_LOCATION must be westus3; no region substitution.'

$root = Split-Path $PSScriptRoot -Parent
$context = Get-LabContext $root $EnvironmentName $SubscriptionId $ResourceGroupName -RequireExisting:($Action -in @('Export', 'Run', 'Down'))
$null = New-Item -ItemType Directory -Path $context.Directory -Force
$lock = [IO.File]::Open((Join-Path $context.Directory 'operation.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$previous = @{}
$locationPushed = $false
try {
    Push-Location -LiteralPath $root
    $locationPushed = $true
    $context = Get-LabContext $root $EnvironmentName $SubscriptionId $ResourceGroupName -RequireExisting:($Action -in @('Export', 'Run', 'Down'))
    Save-LabState $context
    $previous = Set-LabProcessEnvironment $context
    foreach ($key in @('LAB_ALLOW_AZURE', 'LAB_ALLOW_DEPLOY', 'LAB_ALLOW_AFFINITY_CHANGE', 'LAB_ALLOW_DELETE', 'LAB_CONFIRM_RESOURCE_GROUP')) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    }
    $env:LAB_ALLOW_AZURE = 'true'
    $env:LAB_ALLOW_DEPLOY = $AllowDeploy.ToString().ToLowerInvariant()
    $env:LAB_ALLOW_AFFINITY_CHANGE = $AllowAffinityChange.ToString().ToLowerInvariant()
    $env:LAB_ALLOW_DELETE = $AllowDelete.ToString().ToLowerInvariant()
    $env:LAB_CONFIRM_RESOURCE_GROUP = $ConfirmResourceGroup
    switch ($Action) {
        Preflight { Invoke-LabPreflight $context }
        Provision { Invoke-LabProvision $context $Driver }
        Export { $null = Export-LabRuntime $context }
        Run { Invoke-LabClient $context $ClientPath $ClientArguments -AllowFaults:$AllowFaults }
        Down { Remove-LabDeployment $context $Driver $ConfirmResourceGroup }
    }
} finally {
    Restore-LabProcessEnvironment $previous
    if ($locationPushed) { Pop-Location }
    $lock.Dispose()
}
