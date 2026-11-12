// The resources inside the resource group: the storage account and the
// identity the archival workflow uploads with.

@description('The region to create everything in.')
param location string

@description('The subject of the GitHub OIDC tokens the identity trusts.')
param githubSubject string

@description('The storage account\'s name, which must be unique across Azure. The default is stable for a given resource group.')
@minLength(3)
@maxLength(24)
param storageAccountName string = 'webarchive${uniqueString(resourceGroup().id)}'

// Storage Blob Data Contributor: read, write and delete blobs, nothing else
var blobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    // Everything signs in with Entra ID, so account keys and SAS are off.
    // The static website endpoint is still public, because it doesn't use
    // these settings.
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-website-archiver'
  location: location
}

// Lets the archival workflow sign in as the identity with its GitHub OIDC
// token, instead of a stored secret
resource githubCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: identity
  name: 'github-archival-workflow'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: githubSubject
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource blobDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, identity.id, blobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributorRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output storageAccountName string = storageAccount.name
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
output websiteUrl string = storageAccount.properties.primaryEndpoints.web
output clientId string = identity.properties.clientId
