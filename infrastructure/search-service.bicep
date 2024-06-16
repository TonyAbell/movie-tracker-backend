
param location string = resourceGroup().location
var uniqueSuffix = uniqueString(resourceGroup().id)
var serviceName = toLower('${uniqueSuffix}-search-service')

resource searchService 'Microsoft.Search/searchServices@2020-08-01' = {
  name: serviceName
  location: location
  sku: {
    name: 'free' // Free tier
  }
  properties: {
    hostingMode: 'default'
  }
  tags: {
    primaryResourceId: resourceId('Microsoft.Search/searchServices', serviceName)
    marketplaceItemId: 'Microsoft.Search'
  }
}
output searchServiceName string = searchService.name
