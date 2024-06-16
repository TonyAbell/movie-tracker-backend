param location string = resourceGroup().location
param adminPrincipalIds array = []
var uniqueSuffix = uniqueString(resourceGroup().id)
var accountName = toLower('${uniqueSuffix}-cosmos-db')

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: accountName
  location: location
  kind: 'GlobalDocumentDB'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    databaseAccountOfferType: 'Standard'
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    isVirtualNetworkFilterEnabled: false
    virtualNetworkRules: []
    ipRules: []
    minimalTlsVersion: 'Tls12'
    capabilities: [
      {
        name: 'EnableServerless'
      }
      {
        name: 'EnableNoSQLVectorSearch'
      }
    ]
    enableFreeTier: false
    capacity: {
      totalThroughputLimit: 4000
    }
  } 
}



resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: 'database'
  location: location

  properties: {
    resource: {
      id: 'database'
    }
  }
}


resource chatSessionCosmosContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  parent: cosmosDatabase
  name: 'chat-sessions'
  location: location
 
  properties:{
     resource:{
      id: 'chat-sessions'
      partitionKey:{
        kind: 'Hash'
        paths:[
          '/PartitionKey'
        ]
      }
      conflictResolutionPolicy: {
         mode: 'LastWriterWins'
         conflictResolutionPath: '/_ts'
      }
     }
  }
}

resource roleAssignmentAdmin 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for adminPrincipalId in adminPrincipalIds: {
  name: guid(adminPrincipalId, cosmosAccount.id)
  scope: cosmosAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5bd9cd88-fe45-4216-938b-f97437e15450') // https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/databases#documentdb-account-contributor
    principalId: adminPrincipalId
  }
}]


output accountName string = accountName
output databaseName string = 'database'
