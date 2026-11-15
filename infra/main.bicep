// The Azure infrastructure for WebsiteArchiver: a resource group holding a
// storage account, whose static website ($web) serves the snapshots and
// viewer, and a managed identity that the website repo's archival workflow
// signs in as through GitHub OIDC, so no keys or secrets are needed.
//
// Deploy, then turn on static website hosting, which Bicep can't do
// because it's a data-plane setting (see infra/README.md):
//
//   az deployment sub create --location uksouth --template-file infra/main.bicep

targetScope = 'subscription'

@description('The region to create everything in.')
param location string = 'uksouth'

@description('The resource group to create.')
param resourceGroupName string = 'website-archiver'

@description('The GitHub repository, as owner/name, whose workflow runs the archiver.')
param githubRepository string = 'stuartmccoll/stuartmccoll.github.io'

@description('The subject of the GitHub OIDC tokens the identity trusts. The default matches jobs on the default branch, including workflow_run triggers. A job that uses an environment needs repo:<owner/name>:environment:<name> instead.')
param githubSubject string = 'repo:${githubRepository}:ref:refs/heads/main'

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
}

module archive 'modules/archive.bicep' = {
  name: 'archive'
  scope: resourceGroup
  params: {
    location: location
    githubSubject: githubSubject
  }
}

@description('The storage account the archiver uploads to.')
output storageAccountName string = archive.outputs.storageAccountName

@description('The blob endpoint the archiver uploads to.')
output blobEndpoint string = archive.outputs.blobEndpoint

@description('Where the viewer and snapshots are served once static website hosting is on.')
output websiteUrl string = archive.outputs.websiteUrl

@description('The client ID the archival workflow signs in with (AZURE_CLIENT_ID).')
output clientId string = archive.outputs.clientId

@description('The tenant the workflow signs in to (AZURE_TENANT_ID).')
output tenantId string = tenant().tenantId
