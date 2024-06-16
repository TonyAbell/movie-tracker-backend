param location string = resourceGroup().location
param sku string = 'Standard'
@secure()
param open_ai_api_key string
@secure()
param the_movie_db_api_key string
param adminPrincipalIds array = []
var uniqueSuffix = uniqueString(resourceGroup().id)
var vaultName = '${uniqueSuffix}-movie-trac'
param keyName string = 'movie-tracker-key'

resource keyvaultDeployment 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: sku
    }
    accessPolicies: []
    enableSoftDelete: true
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
  
  }
}
resource key 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: keyvaultDeployment
  name: keyName
  properties: {
    kty: 'RSA'
    keyOps: [
      'encrypt'
      'decrypt'
    ]
    keySize: 4096
  }
}


resource secretOpenApiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvaultDeployment
  name: 'OpenAi--Api-Key'
  properties: {
    value:  open_ai_api_key
  }
}



resource secretTheMovieDbApiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvaultDeployment
  name: 'TheMovieDb--Api-Key'
  properties: {
    value:  the_movie_db_api_key
  }
}

resource roleAssignmentAdmin 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for adminPrincipalId in adminPrincipalIds: {
  name: guid(adminPrincipalId, keyvaultDeployment.id)
  scope: keyvaultDeployment
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User role
    principalId: adminPrincipalId
  }
}]

output vaultName string = vaultName
