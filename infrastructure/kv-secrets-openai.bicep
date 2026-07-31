param vaultName string
param openAiAccountName string
param openAiDeploymentName string

resource keyvault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource openAiAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: openAiAccountName
}

resource secretAzureOpenAiEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'AzureOpenAi--Endpoint'
  properties: {
    value: openAiAccount.properties.endpoint
  }
}

resource secretAzureOpenAiApiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'AzureOpenAi--Api-Key'
  properties: {
    value: openAiAccount.listKeys().key1
  }
}

resource secretAzureOpenAiDeployment 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'AzureOpenAi--Deployment'
  properties: {
    value: openAiDeploymentName
  }
}
