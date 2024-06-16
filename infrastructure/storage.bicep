
param location string = resourceGroup().location
var uniqueSuffix = uniqueString(resourceGroup().id)
var blobStorageName = toLower('${uniqueSuffix}movietkr')

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-04-01' = {
  name: blobStorageName
  location: location

  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  
  properties:{
    
     minimumTlsVersion: 'TLS1_2'
     allowBlobPublicAccess: true
     allowSharedKeyAccess: true
     networkAcls: {
       bypass: 'AzureServices'
       virtualNetworkRules:[ ]
       ipRules: [ ]
       defaultAction:'Allow'
     }
     supportsHttpsTrafficOnly:true
     accessTier: 'Hot'
     encryption: {
       services:{
         file:{
           keyType: 'Account'
           enabled: true
         }
         blob: {
           keyType: 'Account'
           enabled: true
         }
         
       }
       keySource:'Microsoft.Storage'
     }
  }
}

var fileSharePath = 'funccontentshare'

resource fileServices 'Microsoft.Storage/storageAccounts/fileServices@2023-04-01' = {
  parent: storageAccount
  name: 'default'
}

resource fileShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-04-01' = {
  parent: fileServices
  name: fileSharePath
}

output fileShareName string = fileSharePath
output botStorageId string = storageAccount.id
output botStorageName string = blobStorageName
output AccountName string = blobStorageName
