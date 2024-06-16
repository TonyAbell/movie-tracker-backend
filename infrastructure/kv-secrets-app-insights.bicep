param vaultName string
param appInsightName string
resource keyvault 'Microsoft.KeyVault/vaults@2023-07-01'  existing = {
  name: vaultName
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightName
}

resource secret1 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'APPINSIGHTS-INSTRUMENTATIONKEY'
  properties: {
    value:  appInsights.properties.InstrumentationKey
  }
}

resource secret2 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyvault
  name: 'APPLICATIONINSIGHTS-CONNECTION-STRING'
  properties: {
    value:  appInsights.properties.ConnectionString
  }
}
