param vaultName string
param cosmosAccountName string
param cosmosDatabaseName string

resource keyvault 'Microsoft.KeyVault/vaults@2023-07-01'  existing = {
  name: vaultName
}

resource documentDB 'Microsoft.DocumentDB/databaseAccounts@2023-11-15' existing = {
  name: cosmosAccountName
}

resource secretCosmosConnectionStrings 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'ConnectionStrings--Cosmos'

  properties: {
    value:  'AccountEndpoint=https://${cosmosAccountName}.documents.azure.com:443/;AccountKey=${listKeys(documentDB.id, '2021-03-01-preview').primaryMasterKey};'
  }
}

resource secretCosmosDbAuthKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'Cosmos--AuthKey'

  properties: {
    value:  listKeys(documentDB.id, '2021-03-01-preview').primaryMasterKey
  }
}

resource secretCosmosDbUrl 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'Cosmos--Endpoint'

  properties: {
    value:  'https://${cosmosAccountName}.documents.azure.com:443/'
  }
}

resource secretCosmosDbDatabaseName 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'Cosmos--DatabaseName'
  properties: {
    value:  cosmosDatabaseName
  }
}


