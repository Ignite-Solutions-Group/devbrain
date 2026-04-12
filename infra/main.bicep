@description('Azure region for all resources.')
param location string = 'eastus'

@description('Environment name used for resource naming (set by azd).')
param environmentName string

// ─── v1.6 OAuth DCR facade parameters ───────────────────────────────────────
//
// These must be set by the deployer (via azd env set) BEFORE first deploy. See CHANGELOG v1.6.0
// and the sprint doc "Deploy prerequisite" callout. Neither value is sensitive — the client secret
// lives in Key Vault, not here.

@description('Entra tenant GUID (single-tenant only — not "common" or "organizations").')
param entraTenantId string

@description('Entra app registration client ID for the single pre-registered DevBrain app.')
param entraClientId string

var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))

// ─── Storage Account (required by Azure Functions) ───────────────────────────

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'sadevbrain${substring(resourceToken, 0, 6)}'
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'deploymentpackage'
  properties: {
    publicAccess: 'None'
  }
}

// ─── Data Protection key ring blob container (v1.6 upstream token encryption) ──
//
// Holds the ASP.NET Core Data Protection key ring (single keys.xml blob). The Function MI
// already has Storage Blob Data Owner at the storage account scope, which covers this container,
// so no new role assignment is required.

resource dataProtectionKeysContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'dataprotection-keys'
  properties: {
    publicAccess: 'None'
  }
}

// ─── Cosmos DB ───────────────────────────────────────────────────────────────

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: 'cosmos-devbrain-${resourceToken}'
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    enableFreeTier: false
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: 'devbrain'
  properties: {
    resource: {
      id: 'devbrain'
    }
  }
}

resource cosmosContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: 'documents'
  properties: {
    resource: {
      id: 'documents'
      partitionKey: {
        paths: ['/key']
        kind: 'Hash'
      }
      // Enable per-item TTL (defaultTtl: -1 means "TTL feature on, but no default
      // expiration"). Real documents have no `ttl` field so they live forever;
      // chunked-upload staging docs (UpsertDocumentChunked) set `ttl` explicitly
      // so abandoned uploads self-clean after a few hours.
      defaultTtl: -1
    }
  }
}

// ─── OAuth state container (v1.6 DCR facade) ────────────────────────────────
//
// Holds the five DCR-facade record kinds: client:{id}, txn:{state}, code:{code},
// upstream:{jti}, refresh:{token}. Every record sets `ttl` explicitly via the
// application code (see CosmosOAuthStateStore). defaultTtl: -1 keeps the TTL
// feature on without a default expiration.

resource cosmosOAuthStateContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: 'oauth_state'
  properties: {
    resource: {
      id: 'oauth_state'
      partitionKey: {
        paths: ['/key']
        kind: 'Hash'
      }
      defaultTtl: -1
    }
  }
}

// ─── Log Analytics + Application Insights ───────────────────────────────────

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-devbrain-${resourceToken}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-devbrain-${resourceToken}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspace.id
  }
}

// ─── Key Vault (v1.6 DCR facade) ────────────────────────────────────────────
//
// Holds two secrets set manually post-deploy (see CHANGELOG v1.6.0 and the sprint doc's
// deploy prerequisite callout):
//   - jwt-signing-secret: base64-encoded 32-byte HMAC key for DevBrain JWT signing.
//     Generate with: `openssl rand -base64 32`
//   - entra-client-secret: the client secret from the tenant-admin-created Entra app registration.
//
// Bicep does NOT create the secret resources themselves — their values come from outside and
// populating them via Bicep parameters would put sensitive material in deployment state.

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  // Compressed form of the {type}devbrain{resourceToken} naming convention. The hyphenated form
  // `kv-devbrain-${resourceToken}` would be 25 chars, one over the KV 24-char limit, so we fall
  // back to the compressed form (consistent with how the storage account is named): 2 + 8 + 13 = 23.
  name: 'kvdevbrain${resourceToken}'
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

// ─── Data Protection master key (v1.6 upstream token encryption) ────────────
//
// ASP.NET Core Data Protection protects its key ring with this key (via wrapKey/unwrapKey).
// The Function MI's Key Vault Crypto User role (granted below) covers the required operations.
// Key rotation here rotates the KEK, not the data keys; DP handles data-key rotation internally.

resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: keyVault
  name: 'data-protection-key'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [
      'wrapKey'
      'unwrapKey'
    ]
  }
}

// ─── Flex Consumption App Service Plan ───────────────────────────────────────

resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-devbrain-${resourceToken}'
  location: location
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

// ─── Function App (Flex Consumption) ─────────────────────────────────────────

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: 'func-devbrain-${resourceToken}'
  location: location
  kind: 'functionapp,linux'
  tags: {
    'azd-service-name': 'api'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    reserved: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.properties.primaryEndpoints.blob}deploymentpackage'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 10
        instanceMemoryMB: 2048
      }
    }
    siteConfig: {
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storageAccount.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'CosmosDb__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
        { name: 'CosmosDb__DatabaseName', value: 'devbrain' }
        { name: 'CosmosDb__ContainerName', value: 'documents' }
        { name: 'CosmosDb__OAuthContainerName', value: 'oauth_state' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsights.properties.ConnectionString }
        // Worker-side log level filters. host.json sets host-process levels; these control the
        // isolated worker's ILogger pipeline (which is what DevBrain code actually writes to).
        // Keep DevBrain at Debug while we're diagnosing the v1.6 post-deploy issues; dial back
        // once the shape of OAuth traffic stabilizes.
        { name: 'Logging__LogLevel__Default', value: 'Information' }
        { name: 'Logging__LogLevel__DevBrain', value: 'Debug' }
        { name: 'Logging__LogLevel__Microsoft', value: 'Warning' }
        { name: 'Logging__LogLevel__Microsoft.Hosting.Lifetime', value: 'Information' }
        // Disable the Application Insights SDK's default adaptive sampling while investigating —
        // sampling can drop the exact invocations we're trying to catch. Re-enable post-stabilization.
        { name: 'APPLICATIONINSIGHTS_SAMPLING_PERCENTAGE', value: '100' }
        // v1.6 OAuth DCR facade. Secrets are Key Vault references — the secret values are set
        // manually post-deploy (see CHANGELOG v1.6.0 and the sprint doc deploy prereqs).
        { name: 'OAuth__BaseUrl', value: 'https://func-devbrain-${resourceToken}.azurewebsites.net' }
        { name: 'OAuth__EntraTenantId', value: entraTenantId }
        { name: 'OAuth__EntraClientId', value: entraClientId }
        { name: 'OAuth__EntraClientSecret', value: '@Microsoft.KeyVault(SecretUri=https://${keyVault.name}${environment().suffixes.keyvaultDns}/secrets/entra-client-secret/)' }
        { name: 'OAuth__JwtSigningSecret', value: '@Microsoft.KeyVault(SecretUri=https://${keyVault.name}${environment().suffixes.keyvaultDns}/secrets/jwt-signing-secret/)' }
        { name: 'KeyVault__Name', value: keyVault.name }
        // v1.6 Data Protection: key ring persisted in blob storage, protected by the KV key above.
        // Used exclusively by IUpstreamTokenProtector (purpose: DevBrain.OAuth.UpstreamToken).
        { name: 'DataProtection__BlobUri', value: '${storageAccount.properties.primaryEndpoints.blob}dataprotection-keys/keys.xml' }
        { name: 'DataProtection__KeyVaultKeyUri', value: '${keyVault.properties.vaultUri}keys/data-protection-key' }
      ]
    }
  }
}

// ─── App Service Authentication: EXPLICITLY DISABLED (v1.6 DCR facade) ──────
//
// DevBrain's DCR facade includes its own JWT validation middleware
// (McpJwtValidationMiddleware) that is the sole gate on MCP tool invocations.
// App Service Authentication (Easy Auth) must be OFF — if it's enabled, it
// intercepts Bearer tokens at the platform layer BEFORE the worker sees them
// and validates against the Entra identity provider's audience/issuer/JWKS,
// which doesn't match DevBrain's own HS256 JWTs. Result: 401 with
// "The audience 'X' is invalid" from Easy Auth, zero logs in App Insights
// (our code never ran), and a hard-to-diagnose silent rejection.
//
// This resource pins Easy Auth OFF in Bicep so it survives `azd up` and
// portal-click accidents. If you need App Service Auth for a different
// purpose in the future, coordinate with the DCR middleware first.

// resource functionAppAuth 'Microsoft.Web/sites/config@2024-04-01' = {
//   parent: functionApp
//   name: 'authsettingsV2'
//   properties: {
//     platform: {
//       enabled: false
//     }
//     globalValidation: {
//       requireAuthentication: false
//       unauthenticatedClientAction: 'AllowAnonymous'
//     }
//     // Empty identityProviders block CLEARS all configured providers. This is load-bearing —
//     // even with platform.enabled = false, the MCP extension's "built-in MCP server authorization"
//     // feature reads identity provider configs and validates Bearer tokens at the webhook level
//     // if any provider is enabled. Our DCR facade has its own JWT validation middleware
//     // (McpJwtValidationMiddleware) that is the sole gate; no App Service-level validation
//     // should be active. See: https://learn.microsoft.com/azure/app-service/configure-authentication-mcp
//     identityProviders: {}
//   }
// }

// ─── Storage Blob Data Owner (for Flex Consumption managed identity) ────────

resource storageBlobDataOwnerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  scope: storageAccount
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  }
}

// ─── Storage Queue Data Contributor (for Flex Consumption managed identity) ─

resource storageQueueDataContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  scope: storageAccount
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  }
}

// ─── Storage Queue Data Message Processor (for Flex Consumption managed identity) ─

resource storageQueueDataMessageProcessorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, '8a0f0c08-91a1-4084-bc3d-661d67233fed')
  scope: storageAccount
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8a0f0c08-91a1-4084-bc3d-661d67233fed')
  }
}

// ─── Monitoring Metrics Publisher (for Function App → App Insights) ────────

resource monitoringMetricsPublisherRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, functionApp.id, '3913510d-42f4-4e42-8a64-420c390055eb')
  scope: applicationInsights
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}

// ─── Cosmos DB RBAC (Managed Identity) ───────────────────────────────────────

// Cosmos DB Built-in Data Contributor role
var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'

resource cosmosRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, functionApp.id, cosmosDataContributorRoleId)
  properties: {
    principalId: functionApp.identity.principalId
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/${cosmosDataContributorRoleId}'
    scope: cosmosAccount.id
  }
}

// ─── Key Vault RBAC (v1.6 — BOTH planes required) ────────────────────────────
//
// Data Protection's secret-backed key ring (used for future upstream-token encryption) needs
// Secrets Officer to create and update secrets, and Crypto User to wrap/unwrap via KEK operations.
// Single-role configurations fail silently until the first key rotation — bake both in on day one.
// See sprint doc "Files to modify → infra/main.bicep".

// Key Vault Crypto User: 12338af0-0e69-4776-bea7-57ae8d297424
resource keyVaultCryptoUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, functionApp.id, '12338af0-0e69-4776-bea7-57ae8d297424')
  scope: keyVault
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')
  }
}

// Key Vault Secrets Officer: b86a8fe4-44ce-4948-aee5-eccb2c155cd7
resource keyVaultSecretsOfficerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, functionApp.id, 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
  scope: keyVault
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
  }
}

// ─── Outputs ─────────────────────────────────────────────────────────────────

output AZURE_FUNCTION_URL string = 'https://${functionApp.properties.defaultHostName}'
output COSMOS_ACCOUNT_ENDPOINT string = cosmosAccount.properties.documentEndpoint
output KEY_VAULT_NAME string = keyVault.name
output KEY_VAULT_URI string = keyVault.properties.vaultUri
