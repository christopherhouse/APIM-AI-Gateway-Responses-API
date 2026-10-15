param(
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [Parameter(Mandatory = $true)][string]$Subscription,
    [Parameter(Mandatory = $true)][string]$Session,
    [Parameter(Mandatory = $true)][datetimeoffset]$Deadline,
    [Parameter(Mandatory = $true)][string]$SignalPath
)

$ErrorActionPreference = 'Stop'
while (-not (Test-Path -LiteralPath $SignalPath) -and [datetimeoffset]::UtcNow -lt $Deadline) {
    Start-Sleep -Seconds 15
}
$groupText = az group show --name $ResourceGroup --subscription $Subscription --output json
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify cleanup ownership; no deletion attempted.' }
$group = $groupText | ConvertFrom-Json
if ($group.name -cne $ResourceGroup -or
    $group.tags.session -cne $Session -or
    $group.tags.purpose -cne 'task001-disposable-feasibility') {
    throw 'Cleanup ownership mismatch; refusing deletion.'
}
Write-Output ('Cleanup started at ' + [datetimeoffset]::UtcNow.ToString('o'))
$activeText = az deployment group list --resource-group $ResourceGroup --subscription $Subscription --query "[?properties.provisioningState=='Running' || properties.provisioningState=='Accepted'].name" --output json
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect deployments before cleanup.' }
foreach ($name in ($activeText | ConvertFrom-Json)) {
    az deployment group cancel --name $name --resource-group $ResourceGroup --subscription $Subscription --output none
    if ($LASTEXITCODE -ne 0) { throw "Could not cancel deployment $name before cleanup." }
}
az group delete --name $ResourceGroup --subscription $Subscription --yes
if ($LASTEXITCODE -ne 0) { throw 'Owned group deletion failed; manual cleanup required.' }
$exists = az group exists --name $ResourceGroup --subscription $Subscription --output tsv
if ($LASTEXITCODE -ne 0 -or $exists -ne 'false') { throw 'Group deletion could not be verified.' }
Write-Output ('Verified owned group absent at ' + [datetimeoffset]::UtcNow.ToString('o'))
