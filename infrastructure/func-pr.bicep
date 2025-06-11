param prNumber string
param keyVaultName string
param appInsightName string
param appServicePlanName string
param createFuncRoleAssignment bool = true
param location string = resourceGroup().location

var uniqueSuffix = uniqueString(resourceGroup().id, prNumber)

var functionAppName = toLower('${uniqueSuffix}-func-movie-tracker-pr-${prNumber}')

resource keyVault 'Microsoft.KeyVault/vaults@2023-02-01' existing = {
  name: keyVaultName
}

resource vaultSecret_AzureWebJobsStorage 'Microsoft.KeyVault/vaults/secrets@2023-02-01' existing = {
  parent: keyVault
  name: 'AzureWebJobsStorage'
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightName
}

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' existing = {
  name: appServicePlanName
  
}

resource functionApp 'Microsoft.Web/sites@2021-01-01' = {
  name: functionAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  kind: 'functionapp'
  properties: {
    serverFarmId: appServicePlan.id
    siteConfig: {
      use32BitWorkerProcess: false
      netFrameworkVersion: 'v9.0'
      appSettings: [
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'AzureWebJobsStorage'
          value: '@Microsoft.KeyVault(SecretUri=${vaultSecret_AzureWebJobsStorage.properties.secretUriWithVersion})'
        }
        // {
        //   name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
        //   value: '@Microsoft.KeyVault(SecretUri=${vaultSecret_AzureWebJobsStorage.properties.secretUriWithVersion})'
        // }
        // {
        //   name: 'WEBSITE_CONTENTSHARE'
        //   value: funcContentShareName
        // }
        {
          name: 'WEBSITE_USE_PLACEHOLDER_DOTNETISOLATED'
          value: '1'
        }
        {
          name: 'VaultUri'
          value: keyVault.properties.vaultUri
        }
        {
          name: 'APPINSIGHTS-INSTRUMENTATIONKEY'
          value: appInsights.properties.InstrumentationKey
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'WEBSITE_ENABLE_SYNC_UPDATE_SITE'
          value: 'true'
        }
      ]
    }
  }
}

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (createFuncRoleAssignment) {
  name: guid(functionApp.id, keyVault.id)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: functionApp.identity.principalId
  }
}

resource appServiceLogging 'Microsoft.Web/sites/config@2023-01-01' = {
  parent: functionApp
  name: 'logs'
  properties: {
    applicationLogs: {
      fileSystem: {
        level: 'Information'
      }
    }
  }
}

output functionAppId string = functionApp.id
output functionAppName string = functionApp.name
