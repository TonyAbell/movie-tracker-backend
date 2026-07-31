// Microsoft/Azure AI Foundry resource (formerly the Azure OpenAI resource for this app).
//
// This is the same underlying Microsoft.CognitiveServices/accounts resource that used to be
// deployed with kind 'OpenAI'; switching kind to 'AIServices' + allowProjectManagement is the
// documented in-place upgrade and is non-destructive:
//   https://learn.microsoft.com/en-us/azure/foundry/how-to/upgrade-azure-openai
// The resource name, custom subdomain, https://<name>.openai.azure.com/ endpoint, API keys and
// existing model deployments all survive, so no application config has to change. The upgrade is
// reversible by setting kind back to 'OpenAI' (after deleting any projects/non-OpenAI deployments).
//
// Why bother: the OpenAI kind can only deploy OpenAI models. The AIServices kind additionally
// unlocks the AIServices.* quota buckets in this subscription (grok-4-1-fast-*, gpt-oss-120b,
// claude-haiku-4-5, DeepSeek, Mistral, ...), which is where the cheap/fast chat models live.
// Both gpt-4.1-nano and gpt-5-nano have zero quota here, so those were not an option.
//
// The resource group is westus2, which offers no OpenAI models at all; westus does.
param location string = 'westus'

// Primary chat model. gpt-5.4-mini replaced gpt-5-mini after a measured head-to-head on this app's
// own prompt and tool set: 29.8s vs 113.2s across the three benchmark queries, and ~$2.16 vs ~$3.55
// per 1000 turns. gpt-5-mini spent 1344 of its 1587 completion tokens per turn on hidden reasoning;
// gpt-5.4-mini emits none at default effort, which is where both the latency and the cost went.
//
// DataZoneStandard, not GlobalStandard: this subscription has 0 GlobalStandard quota for
// gpt-5.4-mini and 200K TPM on DataZoneStandard. That keeps inference in the US data zone and adds
// roughly a 10% token price uplift.
param modelName string = 'gpt-5.4-mini'
param modelVersion string = '2026-03-17'
param deploymentName string = 'gpt-5-4-mini'
param deploymentSku string = 'DataZoneStandard'
param deploymentCapacity int = 200

// Previous model, kept deployed as a fallback: it holds the largest quota pool of any chat model in
// this subscription (500K TPM GlobalStandard) and costs nothing while it serves no traffic.
param fallbackModelName string = 'gpt-5-mini'
param fallbackModelVersion string = '2025-08-07'
param fallbackDeploymentName string = 'gpt-5-mini'
param fallbackDeploymentCapacity int = 100

var uniqueSuffix = uniqueString(resourceGroup().id)
// Name is unchanged from the pre-upgrade Azure OpenAI account on purpose — renaming would mint a
// new endpoint hostname and invalidate the AzureOpenAi--Endpoint secret in Key Vault.
var accountName = toLower('${uniqueSuffix}-movie-tracker-openai')

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: accountName
  location: location
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    // Required for the upgrade; Foundry uses it for its Microsoft-managed storage/key vault.
    type: 'SystemAssigned'
  }
  properties: {
    // Required to work as a Foundry resource (projects, agents, model catalog).
    allowProjectManagement: true
    // Required for both the data-plane endpoint URL and Entra ID auth.
    customSubDomainName: accountName
    publicNetworkAccess: 'Enabled'
    // The docs' sample sets this to true, but the function app authenticates to this account with
    // an API key pulled from Key Vault (AzureOpenAi--Api-Key), so local auth must stay enabled.
    // Flipping this to true breaks every Chat-Ask call.
    disableLocalAuth: false
  }
}

resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: foundryAccount
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
    // NOTE: dynamicThrottlingEnabled (borrow spare regional capacity instead of returning 429 at
    // the assigned TPM ceiling) cannot be set here - the control plane rejects it with
    // "DynamicThrottlingEnabled is not supported for current SKU DataZoneStandard". It is a
    // regional-Standard feature. This matters because Chat-Ask re-walks the whole chat history on
    // every call, so one request against a long conversation is a large token burst; that is
    // exactly how the grok-4-1-fast candidate (50K TPM) hit 429 during benchmarking. The mitigation
    // here is capacity instead: 200K TPM, the full DataZoneStandard quota for this model.
  }
}

resource fallbackModelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: foundryAccount
  name: fallbackDeploymentName
  // Serialized after the primary: the control plane rejects concurrent writes to two deployments
  // on the same account.
  dependsOn: [
    modelDeployment
  ]
  sku: {
    name: 'GlobalStandard'
    capacity: fallbackDeploymentCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: fallbackModelName
      version: fallbackModelVersion
    }
  }
}

output accountName string = foundryAccount.name
// Deliberately the openai.azure.com host, not foundryAccount.properties.endpoint, which on an
// AIServices resource reports the generic cognitiveservices.azure.com FQDN that does not serve the
// /openai/deployments/... route Semantic Kernel calls.
output endpoint string = 'https://${foundryAccount.properties.customSubDomainName}.openai.azure.com/'
output deploymentName string = modelDeployment.name
output fallbackDeploymentName string = fallbackModelDeployment.name
