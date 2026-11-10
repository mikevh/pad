targetScope = 'resourceGroup'

@description('Web app name (globally unique; default hostname <name>.azurewebsites.net).')
param webAppName string = 'pad-mikevh'

@description('Existing App Service plan (Windows, same resource group).')
param planName string = 'mikevhplan'

@description('Storage account name (3-24 lowercase letters/digits, globally unique).')
param storageAccountName string = 'padstoremikevh'

param location string = resourceGroup().location

@description('Region of the App Service plan (West US); the resource group is in West US 2.')
param webLocation string = 'westus'

@description('Public hostname; also the WebAuthn relying party ID.')
param publicHostname string = 'p.zorgen.io'

@secure()
@description('One-time token required to register the first passkey.')
param setupToken string

var filesContainer = 'pad'
var systemContainer = 'pad-system'
// Storage Blob Data Contributor
var blobContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource plan 'Microsoft.Web/serverfarms@2023-12-01' existing = {
  name: planName
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    isHnsEnabled: false
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    defaultToOAuthAuthentication: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource files 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: filesContainer
  properties: { publicAccess: 'None' }
}

resource system 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: systemContainer
  properties: { publicAccess: 'None' }
}

resource web 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  // Must match the plan's region (West US), which differs from the resource group's.
  location: webLocation
  kind: 'app'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'Storage__AccountName', value: storage.name }
        { name: 'Storage__FilesContainer', value: filesContainer }
        { name: 'Storage__SystemContainer', value: systemContainer }
        { name: 'Auth__RelyingPartyId', value: publicHostname }
        { name: 'Auth__Origin', value: 'https://${publicHostname}' }
        { name: 'Auth__SetupToken', value: setupToken }
      ]
    }
  }
}

resource blobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, web.id, blobContributorRoleId)
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributorRoleId)
  }
}

output webAppDefaultHostname string = web.properties.defaultHostName
output customDomainVerificationId string = web.properties.customDomainVerificationId
output principalId string = web.identity.principalId
