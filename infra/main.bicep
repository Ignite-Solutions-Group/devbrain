@description('Azure region for all resources.')
param location string = 'eastus'

@description('Environment name used for resource naming (set by azd).')
param environmentName string

@description('Entra app registration client ID for the DevBrain application.')
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
    }
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
    reserved: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10.0'
      appSettings: [
        { name: 'AzureWebJobsStorage', value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}' }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'CosmosDb__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
        { name: 'CosmosDb__DatabaseName', value: 'devbrain' }
        { name: 'CosmosDb__ContainerName', value: 'documents' }
      ]
    }
  }
}

// ─── Easy Auth (Entra ID) ────────────────────────────────────────────────────

resource functionAppAuth 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: functionApp
  name: 'authsettingsV2'
  properties: {
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: entraClientId
          openIdIssuer: 'https://login.microsoftonline.com/${tenant().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            'api://${entraClientId}'
          ]
        }
      }
    }
    platform: {
      enabled: true
    }
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

// ─── Outputs ─────────────────────────────────────────────────────────────────

output AZURE_FUNCTION_URL string = 'https://${functionApp.properties.defaultHostName}'
output COSMOS_ACCOUNT_ENDPOINT string = cosmosAccount.properties.documentEndpoint
