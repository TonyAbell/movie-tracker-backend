
param location string = resourceGroup().location
@secure()
param open_ai_api_key string
param adminPrincipalIds array = []
module KeyVaultModule 'key-vault.bicep' = {
  name: 'KeyVault_Deploy'
  params: { 
    location: location
    open_ai_api_key:open_ai_api_key
    adminPrincipalIds:adminPrincipalIds
  }
}

module appInsightModule 'app-insights.bicep' ={
  name: 'AppInsights_Deploy'
  params:  { 
     location:location
  }
}

module kvSecretAppInsights 'kv-secrets-app-insights.bicep' = {
  name: 'KeyVault_Secret_AppInsights_Deploy'
  params: {
    appInsightName: appInsightModule.outputs.appInsightName
    vaultName: KeyVaultModule.outputs.vaultName
  }
}


module StorageModule 'storage.bicep' = {
  name: 'Storage_Deploy'
  params:  { 
    location:location
 }
}

module storageKVModule 'kv-secrets-storage.bicep' = {
  name: 'KeyVault_Secrets_Storage_Deploy'
   params: {
    storageAccountName: StorageModule.outputs.AccountName
    vaultName: KeyVaultModule.outputs.vaultName
   }
}



module FuncModule 'func.bicep' = {
  name: 'Func_Deploy'
 
  params: {
    keyVaultName: KeyVaultModule.outputs.vaultName
    location: location
    appInsightName: appInsightModule.outputs.appInsightName
    funcContentShareName: StorageModule.outputs.fileShareName
  }
}

module CosmosModule 'cosmos.bicep' = {
  name: 'Cosmos_Deploy'
  params: {
    location: location
  }
}

module CosmosKVModule 'kv-secrets-cosmosdb.bicep' = {
  name: 'CosmosDB_KeyVault_Secrets_Deploy'
  params: {
    cosmosAccountName: CosmosModule.outputs.accountName
    cosmosDatabaseName: CosmosModule.outputs.databaseName
    vaultName: KeyVaultModule.outputs.vaultName
  }
}

module SearchService 'search-service.bicep' = {
  name: 'SearchService_Deploy'
  params: {
    location: location
  }
}

module SearchServiceKVModule 'kv-secrets-search-service.bicep' = {
  name: 'SearchService_KeyVault_Secrets_Deploy'
  params: {
    searchServiceName: SearchService.outputs.searchServiceName
    vaultName: KeyVaultModule.outputs.vaultName
  }
}
