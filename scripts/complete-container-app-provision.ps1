[CmdletBinding()]
param(
    [string]$SubscriptionId,
    [string]$ResourceGroup,
    [string]$CosmosAccountId,
    [string]$CosmosAccountName,
    [string]$PrincipalId,
    [string]$RoleAssignmentId,
    [ValidateRange(1, 60)]
    [int]$MaximumAttempts = 12,
    [ValidateRange(1, 60)]
    [int]$RetryDelaySeconds = 10,
    [ValidateRange(0, 300)]
    [int]$FunctionWarmupSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-AzdValue {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [string]$ExplicitValue
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitValue)) {
        return $ExplicitValue.Trim()
    }

    $environmentValue = [Environment]::GetEnvironmentVariable($Name)
    if (-not [string]::IsNullOrWhiteSpace($environmentValue)) {
        return $environmentValue.Trim()
    }

    $azdValue = & azd env get-value $Name 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($azdValue)) {
        throw "Required azd value '$Name' is unavailable. Run this script from a provisioned azd environment or pass the corresponding parameter explicitly."
    }

    return $azdValue.Trim()
}

$SubscriptionId = Resolve-AzdValue -Name 'AZURE_SUBSCRIPTION_ID' -ExplicitValue $SubscriptionId
$ResourceGroup = Resolve-AzdValue -Name 'AZURE_RESOURCE_GROUP' -ExplicitValue $ResourceGroup
$CosmosAccountId = Resolve-AzdValue -Name 'AZURE_COSMOS_ACCOUNT_ID' -ExplicitValue $CosmosAccountId
$CosmosAccountName = Resolve-AzdValue -Name 'AZURE_COSMOS_ACCOUNT_NAME' -ExplicitValue $CosmosAccountName
$PrincipalId = Resolve-AzdValue -Name 'AZURE_CONTAINER_APP_IDENTITY_PRINCIPAL_ID' -ExplicitValue $PrincipalId
$RoleAssignmentId = Resolve-AzdValue -Name 'AZURE_CONTAINER_APP_COSMOS_ROLE_ASSIGNMENT_ID' -ExplicitValue $RoleAssignmentId

if (-not [guid]::TryParse($SubscriptionId, [ref]([guid]::Empty))) {
    throw 'AZURE_SUBSCRIPTION_ID must be a GUID.'
}

if (-not [guid]::TryParse($PrincipalId, [ref]([guid]::Empty))) {
    throw 'AZURE_CONTAINER_APP_IDENTITY_PRINCIPAL_ID must be a GUID.'
}

if (-not [guid]::TryParse($RoleAssignmentId, [ref]([guid]::Empty))) {
    throw 'AZURE_CONTAINER_APP_COSMOS_ROLE_ASSIGNMENT_ID must be a GUID.'
}

$expectedAccountId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.DocumentDB/databaseAccounts/$CosmosAccountName"
if (-not $CosmosAccountId.Equals($expectedAccountId, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'AZURE_COSMOS_ACCOUNT_ID does not match the selected subscription, resource group, and account name.'
}

$roleDefinitionId = "$CosmosAccountId/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
$requestUri = "https://management.azure.com$CosmosAccountId/sqlRoleAssignments/$RoleAssignmentId`?api-version=2024-05-15"
$requestBody = @{
    properties = @{
        principalId = $PrincipalId
        roleDefinitionId = $roleDefinitionId
        scope = $CosmosAccountId
    }
} | ConvertTo-Json -Depth 4 -Compress

$managementToken = (& azd auth token --scope 'https://management.azure.com/.default').Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($managementToken)) {
    throw 'Unable to obtain an Azure Resource Manager token from azd.'
}

$headers = @{
    Authorization = "Bearer $managementToken"
    'Content-Type' = 'application/json'
}

for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
    try {
        Invoke-RestMethod -Method Put -Uri $requestUri -Headers $headers -Body $requestBody | Out-Null
        Write-Host "Cosmos DB data-role assignment is ready for managed identity $PrincipalId."
        break
    }
    catch {
        $details = @($_.ErrorDetails.Message, $_.Exception.Message) -join ' '
        $isPropagationDelay = $details -match '(?i)principal.*(not found|does not exist)'

        if (-not $isPropagationDelay -or $attempt -eq $MaximumAttempts) {
            throw
        }

        Write-Warning "Managed identity is not visible to Cosmos DB yet (attempt $attempt of $MaximumAttempts). Retrying in $RetryDelaySeconds seconds."
        Start-Sleep -Seconds $RetryDelaySeconds
    }
}

if ($FunctionWarmupSeconds -gt 0) {
    Write-Host "Waiting $FunctionWarmupSeconds seconds for the Functions host to settle before deployment."
    Start-Sleep -Seconds $FunctionWarmupSeconds
}
