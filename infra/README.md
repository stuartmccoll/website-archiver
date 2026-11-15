# Infrastructure

[`main.bicep`](main.bicep) creates everything WebsiteArchiver needs in Azure:

- a resource group, `website-archiver`, in `uksouth`
- a storage account whose static website (the `$web` container) serves the viewer and snapshots. Account keys and SAS are turned off, so everything signs in with Microsoft Entra ID
- a managed identity, `id-website-archiver`, which the archival workflow in `stuartmccoll/stuartmccoll.github.io` signs in as through GitHub OIDC, so no secrets are stored. It can read and write blobs in the storage account, and nothing else

## Deploying

You need the Azure CLI, signed in (`az login`) as someone who can create role assignments in the subscription, such as an Owner.

1. Deploy the template, from the repo root:

   ```sh
   az deployment sub create \
     --name website-archiver \
     --location uksouth \
     --template-file infra/main.bicep \
     --query properties.outputs
   ```

2. Turn on static website hosting. Bicep can't do this, because it's a data-plane setting, not part of the storage account resource. Use the `storageAccountName` output:

   ```sh
   az storage blob service-properties update \
     --auth-mode login \
     --account-name <storageAccountName> \
     --static-website \
     --index-document index.html
   ```

   This creates the `$web` container. The archiver uploads to it, so set `AZURE_BLOB_STORAGE_CONTAINER_NAME` to `$web`.

The deployment can be run again safely; it changes only what differs from the template. Step 2 only needs running once.

## Outputs

| Output | Used for |
|---|---|
| `storageAccountName` | The storage account, for step 2 |
| `blobEndpoint` | The endpoint the archiver uploads to |
| `websiteUrl` | Where the viewer is served, e.g. `https://<account>.z33.web.core.windows.net/` |
| `clientId`, `tenantId` | The archiver's `AZURE_CLIENT_ID` and `AZURE_TENANT_ID`. Neither is a secret |

The archival workflow runs the archiver with `AZURE_BLOB_STORAGE_ENDPOINT` set to `blobEndpoint`, `AZURE_BLOB_STORAGE_CONTAINER_NAME` set to `$web`, and `AZURE_FEDERATED_TOKEN_FILE` pointing at its GitHub OIDC token, with audience `api://AzureADTokenExchange`. See the Configuration section of the main README.

The identity only trusts workflow runs on the website repo's `main` branch, including runs triggered by `workflow_run`. If the workflow's job uses a GitHub environment, deploy with `--parameters githubSubject=repo:stuartmccoll/stuartmccoll.github.io:environment:<name>` instead.
