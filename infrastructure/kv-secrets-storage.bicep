param vaultName string
param storageAccountName string

resource keyvault 'Microsoft.KeyVault/vaults@2023-07-01'  existing = {
  name: vaultName
}

resource storageResource 'Microsoft.Storage/storageAccounts@2022-09-01' existing = {
  name: storageAccountName

}
resource secretStorage 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'ConnectionStrings--BlobStore'

  properties: {
    value:  'DefaultEndpointsProtocol=https;AccountName=${storageAccountName};AccountKey=${listKeys(storageResource.id,'2021-01-01').keys[0].value};EndpointSuffix=core.windows.net'
  }
}


resource secretStorageWebJob 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'AzureWebJobsStorage'
  dependsOn:[
    secretStorage
  ]

  properties: {
    value:  'DefaultEndpointsProtocol=https;AccountName=${storageAccountName};AccountKey=${listKeys(storageResource.id,'2021-01-01').keys[0].value};EndpointSuffix=core.windows.net'
  }
}

