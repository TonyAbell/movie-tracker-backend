param vaultName string
param searchServiceName string

resource keyvault 'Microsoft.KeyVault/vaults@2023-07-01'  existing = {
  name: vaultName
}

resource searchService 'Microsoft.Search/searchServices@2020-08-01' existing= {
  name: searchServiceName
}

resource secretSearchServiceAdminKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'SearchService--AdminKey'

  properties: {
    value:  searchService.listAdminKeys().primaryKey
  }
}
resource secretSearchServiceUrl 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'SearchService--Url'

  properties: {
    value:  'https://${searchService.name}.search.windows.net'
  }
}
