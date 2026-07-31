param vaultName string
param openAiAccountName string
param openAiDeploymentName string

// Passed through to OpenAIPromptExecutionSettings.ReasoningEffort in Chat-Ask. Valid values differ
// per model family ('minimal' on gpt-5/gpt-5-mini, 'low'/'medium'/'high' more broadly) and models
// without a reasoning stage reject the parameter, so 'default' means "omit it entirely".
// gpt-5.4-mini already emits no reasoning tokens at default effort, hence the default here.
@allowed(['default', 'minimal', 'low', 'medium', 'high'])
param reasoningEffort string = 'default'

resource keyvault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource openAiAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: openAiAccountName
}

// Do NOT use openAiAccount.properties.endpoint here. Now that the account is a Foundry
// (kind 'AIServices') resource it exposes three FQDNs, and properties.endpoint reports the
// generic https://<name>.cognitiveservices.azure.com/ one. Semantic Kernel's
// AddAzureOpenAIChatCompletion appends /openai/deployments/<deployment>/chat/completions, which is
// only served on the openai.azure.com host, so writing properties.endpoint into this secret would
// break every Chat-Ask call on the next deployment of main.bicep.
var azureOpenAiEndpoint = 'https://${openAiAccount.properties.customSubDomainName}.openai.azure.com/'

resource secretAzureOpenAiEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'AzureOpenAi--Endpoint'
  properties: {
    value: azureOpenAiEndpoint
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

resource secretAzureOpenAiReasoningEffort 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'AzureOpenAi--Reasoning-Effort'
  properties: {
    value: reasoningEffort
  }
}
