param location string = resourceGroup().location


var uniqueSuffix = uniqueString(resourceGroup().id)
var appInsightName = toLower('${uniqueSuffix}-movie-tracker')


var logAnalyticsName = toLower('${uniqueSuffix}-movie-tracker')
resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightName
  location: location
  kind: 'string'
  properties: {
    Application_Type:  'web'
    WorkspaceResourceId: logAnalyticsWorkspace.id
  }
}



output applicationId string = appInsights.properties.ApplicationId
output instrumentationKey string = appInsights.properties.InstrumentationKey
output appInsightName string = appInsightName
