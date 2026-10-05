@description('Azure region for all resources.')
param location string = 'eastus'

@description('Environment name used for resource naming (set by azd).')
param environmentName string

// ─── OAuth DCR facade parameters ────────────────────────────────────────────
//
// Tenant and client IDs must be set by the deployer before first provision. Secret bootstrap values
// use separate secure parameters below and are stored in Key Vault.

@description('Entra tenant GUID (single-tenant only — not "common" or "organizations").')
@minLength(1)
param entraTenantId string

@description('Entra app registration client ID for the single pre-registered DevBrain app.')
@minLength(1)
param entraClientId string

@secure()
@description('Optional Entra client secret value to seed into Key Vault on first provision. Leave empty when the secret already exists.')
param entraClientSecretValue string = ''

@secure()
@description('Optional base64-encoded 32-byte JWT signing secret to seed into Key Vault on first provision. Leave empty when the secret already exists.')
param jwtSigningSecretValue string = ''

@description('Optional local DevBrain access-token lifetime in whole minutes. Leave empty for the application default.')
param oauthAccessTokenLifetimeMinutes string = ''

@description('Optional rotated-refresh-token replay marker lifetime in whole minutes. Leave empty for the application default.')
param oauthRefreshReplayLifetimeMinutes string = ''

// The placeholder listens on 8080 and serves /healthz, so the first revision passes the same ingress
// port and probes as DevBrain itself before azd deploys the real image.
@description('Container image used when provisioning the Container App. azd replaces it with the built DevBrain image during deployment.')
param containerAppImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

@description('Minimum Container App replica count. Set to 1 or higher when interactive cold-start latency is important.')
@minValue(0)
param containerAppMinReplicas int = 0

@description('Maximum Container App replica count.')
@minValue(1)
param containerAppMaxReplicas int = 3

@description('Create the Cosmos DB account in serverless (pay-per-request) mode instead of provisioned throughput. Useful when the subscription\'s one free-tier account is already taken. Applies only when the account is first created; Cosmos DB cannot switch an existing account to serverless.')
param cosmosServerless bool = false

// ─── Optional public host name and Azure Front Door ─────────────────────────

@description('Optional public host name, for example devbrain.contoso.com. When set, OAuth URLs and the Entra redirect URI use it instead of the Container Apps host name.')
param customDomainName string = ''

@description('Optional existing Azure Front Door Standard/Premium profile to publish DevBrain through. Requires customDomainName. The app then rejects requests that did not come through this profile.')
param frontDoorProfileName string = ''

@description('Resource group of the Front Door profile. Defaults to this deployment\'s resource group.')
param frontDoorResourceGroupName string = ''

@description('Subscription ID of the Front Door profile. Defaults to this deployment\'s subscription.')
param frontDoorSubscriptionId string = ''

@description('SKU of the existing Front Door profile; the DevBrain WAF policy is created with the matching SKU.')
@allowed([
  'Standard_AzureFrontDoor'
  'Premium_AzureFrontDoor'
])
param frontDoorSkuName string = 'Standard_AzureFrontDoor'

@description('Optional Azure DNS zone that hosts customDomainName. When set with Front Door, the CNAME and domain-validation TXT records are created there.')
param dnsZoneName string = ''

@description('Resource group of the Azure DNS zone. Defaults to the Front Door resource group.')
param dnsZoneResourceGroupName string = ''

@description('Subscription ID of the Azure DNS zone. Defaults to the Front Door subscription.')
param dnsZoneSubscriptionId string = ''

@description('Optional comma-separated two-letter country codes the Front Door WAF allows, for example "US,CA". Empty allows every country.')
param frontDoorAllowedCountryCodes string = ''

var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))

// ─── Storage Account (Data Protection key ring) ─────────────────────────────

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

// ─── Data Protection key ring blob container ────────────────────────────────
//
// Holds the ASP.NET Core Data Protection key ring (single keys.xml blob) that encrypts upstream
// Entra tokens at rest. The name keeps its original `-v2` suffix so existing deployments keep
// their key ring (and live OAuth sessions) across the Functions host retirement.

resource dataProtectionV2KeysContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'dataprotection-keys-v2'
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
    capabilities: cosmosServerless ? [{ name: 'EnableServerless' }] : []
    enableAutomaticFailover: true
    minimalTlsVersion: 'Tls12'
    defaultIdentity: 'FirstPartyIdentity'
    analyticalStorageConfiguration: {
      schemaType: 'WellDefined'
    }
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

// ─── OAuth state container (DCR facade) ─────────────────────────────────────
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

// ─── Container Apps hosting ─────────────────────────────────────────────────

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'acrdevbrain${substring(resourceToken, 0, 6)}'
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
    policies: {
      quarantinePolicy: {
        status: 'disabled'
      }
      retentionPolicy: {
        days: 7
        status: 'disabled'
      }
      trustPolicy: {
        type: 'Notary'
        status: 'disabled'
      }
    }
  }
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-devbrain-${substring(resourceToken, 0, 6)}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsWorkspace.properties.customerId
        sharedKey: logAnalyticsWorkspace.listKeys().primarySharedKey
      }
    }
  }
}

resource containerAppIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-devbrain-${substring(resourceToken, 0, 6)}'
  location: location
}

// ─── Key Vault (DCR facade) ─────────────────────────────────────────────────
//
// Holds two secrets managed outside the application runtime:
//   - jwt-signing-secret: base64-encoded 32-byte HMAC key for DevBrain JWT signing.
//     Generate with: `openssl rand -base64 32`
//   - entra-client-secret: the client secret from the tenant-admin-created Entra app registration.
//
// Existing deployments leave the secure seed parameters empty so Bicep does not replace either
// secret. A new deployment may supply them once through secure azd environment values.

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

// ─── Data Protection master key (upstream token encryption) ─────────────────
//
// ASP.NET Core Data Protection protects its key ring with this key (via wrapKey/unwrapKey).
// The Container App identity's Key Vault Crypto User role (granted below) covers the required operations.
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

resource entraClientSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(entraClientSecretValue)) {
  parent: keyVault
  name: 'entra-client-secret'
  properties: {
    value: entraClientSecretValue
  }
}

resource jwtSigningSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(jwtSigningSecretValue)) {
  parent: keyVault
  name: 'jwt-signing-secret'
  properties: {
    value: jwtSigningSecretValue
  }
}

var containerAppName = 'ca-devbrain-${substring(resourceToken, 0, 6)}'
var containerAppHostName = '${containerAppName}.${containerAppsEnvironment.properties.defaultDomain}'
var containerAppBaseUrl = 'https://${containerAppHostName}'

// Front Door forwards with the Container Apps host name as the Host header, so AllowedHosts keeps it
// alongside the public name.
var publicBaseUrl = empty(customDomainName) ? containerAppBaseUrl : 'https://${customDomainName}'
var allowedHosts = empty(customDomainName) ? containerAppHostName : '${customDomainName};${containerAppHostName}'

// The Front Door profile and DNS zone may live in other subscriptions of the same tenant, for example a
// shared edge subscription and a shared DNS subscription.
var useFrontDoor = !empty(frontDoorProfileName) && !empty(customDomainName)
var frontDoorSubscription = empty(frontDoorSubscriptionId) ? subscription().subscriptionId : frontDoorSubscriptionId
var frontDoorResourceGroup = empty(frontDoorResourceGroupName) ? resourceGroup().name : frontDoorResourceGroupName
var useFrontDoorDns = useFrontDoor && !empty(dnsZoneName)
var dnsZoneSubscription = empty(dnsZoneSubscriptionId) ? frontDoorSubscription : dnsZoneSubscriptionId
var dnsZoneResourceGroup = empty(dnsZoneResourceGroupName) ? frontDoorResourceGroup : dnsZoneResourceGroupName
var normalizedDnsZoneName = toLower(dnsZoneName)
var normalizedCustomDomainName = toLower(customDomainName)

module frontDoor 'modules/front-door.bicep' = if (useFrontDoor) {
  name: 'devbrain-front-door-${substring(resourceToken, 0, 6)}'
  scope: resourceGroup(frontDoorSubscription, frontDoorResourceGroup)
  params: {
    profileName: frontDoorProfileName
    skuName: frontDoorSkuName
    resourceToken: substring(resourceToken, 0, 6)
    customDomainName: normalizedCustomDomainName
    originHostName: containerAppHostName
    dnsZoneId: useFrontDoorDns ? resourceId(dnsZoneSubscription, dnsZoneResourceGroup, 'Microsoft.Network/dnsZones', normalizedDnsZoneName) : ''
    allowedCountryCodes: empty(frontDoorAllowedCountryCodes) ? [] : map(split(frontDoorAllowedCountryCodes, ','), code => toUpper(trim(code)))
  }
}

module frontDoorDns 'modules/front-door-dns.bicep' = if (useFrontDoorDns) {
  name: 'devbrain-front-door-dns-${substring(resourceToken, 0, 6)}'
  scope: resourceGroup(dnsZoneSubscription, dnsZoneResourceGroup)
  params: {
    dnsZoneName: normalizedDnsZoneName
    // The custom domain must sit below the zone apex, for example devbrain.contoso.com in contoso.com.
    recordName: useFrontDoorDns ? substring(normalizedCustomDomainName, 0, length(normalizedCustomDomainName) - length(normalizedDnsZoneName) - 1) : ''
    endpointHostName: useFrontDoor ? frontDoor!.outputs.endpointHostName : ''
    validationToken: useFrontDoor ? frontDoor!.outputs.customDomainValidationToken : ''
  }
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  tags: {
    'azd-service-name': 'server'
  }
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${containerAppIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: containerRegistry.properties.loginServer
          identity: containerAppIdentity.id
        }
      ]
      ingress: {
        external: true
        allowInsecure: false
        targetPort: 8080
        transport: 'auto'
      }
      // The public placeholder image keeps initial provisioning independent of ACR. The registry uses
      // the user-assigned identity because its AcrPull grant can exist before the app is created; a
      // system-assigned identity only exists once the app does, which stalls the first provision.
      secrets: [
        {
          name: 'entra-client-secret'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/entra-client-secret'
          identity: containerAppIdentity.id
        }
        {
          name: 'jwt-signing-secret'
          keyVaultUrl: '${keyVault.properties.vaultUri}secrets/jwt-signing-secret'
          identity: containerAppIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'server'
          image: containerAppImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
            { name: 'AZURE_CLIENT_ID', value: containerAppIdentity.properties.clientId }
            { name: 'AllowedHosts', value: allowedHosts }
            { name: 'CosmosDb__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
            { name: 'CosmosDb__DatabaseName', value: 'devbrain' }
            { name: 'CosmosDb__ContainerName', value: 'documents' }
            { name: 'CosmosDb__OAuthContainerName', value: 'oauth_state' }
            // Namespaces OAuth records in the shared oauth_state container. Kept as `v2:` so existing
            // deployments keep their live sessions; changing it signs every client out.
            { name: 'CosmosDb__OAuthKeyPrefix', value: 'v2:' }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsights.properties.ConnectionString }
            { name: 'OAuth__BaseUrl', value: publicBaseUrl }
            { name: 'FrontDoor__Id', value: useFrontDoor ? frontDoor!.outputs.frontDoorId : '' }
            { name: 'OAuth__EntraTenantId', value: entraTenantId }
            { name: 'OAuth__EntraClientId', value: entraClientId }
            { name: 'OAuth__EntraClientSecret', secretRef: 'entra-client-secret' }
            { name: 'OAuth__JwtSigningSecret', secretRef: 'jwt-signing-secret' }
            { name: 'OAuth__AccessTokenLifetimeMinutes', value: oauthAccessTokenLifetimeMinutes }
            { name: 'OAuth__RefreshReplayLifetimeMinutes', value: oauthRefreshReplayLifetimeMinutes }
            { name: 'DataProtection__BlobUri', value: '${storageAccount.properties.primaryEndpoints.blob}dataprotection-keys-v2/keys.xml' }
            { name: 'DataProtection__KeyVaultKeyUri', value: '${keyVault.properties.vaultUri}keys/data-protection-key' }
            { name: 'RateLimit__PermitLimit', value: '120' }
            { name: 'RateLimit__WindowSeconds', value: '60' }
            { name: 'Server__MaxRequestBodySizeBytes', value: '4194304' }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/healthz'
                port: 8080
                scheme: 'HTTP'
                httpHeaders: [
                  { name: 'Host', value: containerAppHostName }
                ]
              }
              initialDelaySeconds: 1
              periodSeconds: 5
              timeoutSeconds: 3
              failureThreshold: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/healthz'
                port: 8080
                scheme: 'HTTP'
                httpHeaders: [
                  { name: 'Host', value: containerAppHostName }
                ]
              }
              periodSeconds: 5
              timeoutSeconds: 3
              failureThreshold: 3
              successThreshold: 1
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
                scheme: 'HTTP'
                httpHeaders: [
                  { name: 'Host', value: containerAppHostName }
                ]
              }
              initialDelaySeconds: 15
              periodSeconds: 10
              timeoutSeconds: 3
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: containerAppMinReplicas
        maxReplicas: containerAppMaxReplicas
      }
    }
  }
  dependsOn: [
    containerAppIdentityAcrPullRole
    containerAppStorageBlobDataOwnerRole
    containerAppKeyVaultCryptoUserRole
    containerAppKeyVaultSecretsUserRole
    entraClientSecret
    jwtSigningSecret
  ]
}

// ─── Cosmos DB RBAC (Managed Identity) ───────────────────────────────────────

// Cosmos DB Built-in Data Contributor role
var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'
var containerAppCosmosRoleAssignmentName = guid(cosmosAccount.id, containerAppIdentity.id, cosmosDataContributorRoleId)

// ─── Container App managed-identity access ──────────────────────────────────

resource containerAppAcrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, containerApp.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: containerRegistry
  properties: {
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource containerAppIdentityAcrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, containerAppIdentity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: containerRegistry
  properties: {
    principalId: containerAppIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource containerAppStorageBlobDataOwnerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, containerAppIdentity.id, 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  scope: storageAccount
  properties: {
    principalId: containerAppIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  }
}

// Cosmos native SQL role assignments do not accept principalType. A newly-created managed
// identity can therefore be rejected while it is still replicating through Entra. The azd
// postprovision hook creates this deterministic assignment with a bounded, idempotent retry.

resource containerAppKeyVaultCryptoUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, containerAppIdentity.id, '12338af0-0e69-4776-bea7-57ae8d297424')
  scope: keyVault
  properties: {
    principalId: containerAppIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')
  }
}

resource containerAppKeyVaultSecretsUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, containerAppIdentity.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: keyVault
  properties: {
    principalId: containerAppIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

// ─── Outputs ─────────────────────────────────────────────────────────────────

output AZURE_CONTAINER_APP_URL string = containerAppBaseUrl
output DEVBRAIN_PUBLIC_URL string = publicBaseUrl
output DEVBRAIN_MCP_URL string = '${publicBaseUrl}/mcp'
output OAUTH_REDIRECT_URI string = '${publicBaseUrl}/callback'
output FRONT_DOOR_ENDPOINT_HOST_NAME string = useFrontDoor ? frontDoor!.outputs.endpointHostName : ''
output FRONT_DOOR_DOMAIN_VALIDATION_TOKEN string = useFrontDoor ? frontDoor!.outputs.customDomainValidationToken : ''
output AZURE_CONTAINER_REGISTRY_NAME string = containerRegistry.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = containerRegistry.properties.loginServer
output AZURE_CONTAINER_APP_IDENTITY_PRINCIPAL_ID string = containerAppIdentity.properties.principalId
output AZURE_CONTAINER_APP_COSMOS_ROLE_ASSIGNMENT_ID string = containerAppCosmosRoleAssignmentName
output AZURE_COSMOS_ACCOUNT_ID string = cosmosAccount.id
output AZURE_COSMOS_ACCOUNT_NAME string = cosmosAccount.name
output AZURE_KEY_VAULT_NAME string = keyVault.name
output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = logAnalyticsWorkspace.id
output AZURE_RESOURCE_GROUP string = resourceGroup().name
output COSMOS_ACCOUNT_ENDPOINT string = cosmosAccount.properties.documentEndpoint
output KEY_VAULT_NAME string = keyVault.name
output KEY_VAULT_URI string = keyVault.properties.vaultUri
