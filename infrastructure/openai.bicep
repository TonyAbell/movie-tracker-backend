// The resource group is westus2, which offers no Azure OpenAI models at all; westus does.
// gpt-4o is past its deployment cutoff and this subscription has no GlobalStandard gpt-4o
// quota anyway. gpt-5-mini is GA in westus with 500 (K TPM) of GlobalStandard quota.
param location string = 'westus'
param modelName string = 'gpt-5-mini'
param modelVersion string = '2025-08-07'
param deploymentName string = 'gpt-5-mini'
param deploymentSku string = 'GlobalStandard'
param deploymentCapacity int = 100

var uniqueSuffix = uniqueString(resourceGroup().id)
var accountName = toLower('${uniqueSuffix}-movie-tracker-openai')

resource openAiAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: accountName
  location: location
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    // Required for both the data-plane endpoint URL and Entra ID auth.
    customSubDomainName: accountName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
}

resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAiAccount
  name: deploymentName
  sku: {
    name: deploymentSku
    capacity: deploymentCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
  }
}

output accountName string = openAiAccount.name
output endpoint string = openAiAccount.properties.endpoint
output deploymentName string = modelDeployment.name
