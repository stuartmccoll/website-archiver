using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace WebsiteArchiver
{
    /// <summary>
    /// Uploads to the blob container named by
    /// AZURE_BLOB_STORAGE_CONTAINER_NAME. The storage account is given
    /// either by AZURE_BLOB_STORAGE_CONNECTION_STRING, or by
    /// AZURE_BLOB_STORAGE_ENDPOINT, which signs in with workload identity
    /// (AZURE_TENANT_ID, AZURE_CLIENT_ID and AZURE_FEDERATED_TOKEN_FILE),
    /// as the archival workflow does with its GitHub OIDC token.
    /// </summary>
    class AzureBlobStorageClient
    {
        private const int MaxUpdateAttempts = 10;

        private const string ConnectionStringVariable = "AZURE_BLOB_STORAGE_CONNECTION_STRING";
        private const string EndpointVariable = "AZURE_BLOB_STORAGE_ENDPOINT";
        private const string ContainerNameVariable = "AZURE_BLOB_STORAGE_CONTAINER_NAME";
        private const string TokenFileVariable = "AZURE_FEDERATED_TOKEN_FILE";

        // The variables WorkloadIdentityCredential reads
        private static readonly string[] WorkloadIdentityVariables =
        {
            "AZURE_TENANT_ID",
            "AZURE_CLIENT_ID",
            TokenFileVariable,
        };

        // The scope the Blob Storage SDK requests tokens for, so the token
        // fetched by SignIn is the one its uploads reuse
        private static readonly TokenRequestContext StorageTokenRequest = new(
            new[] { "https://storage.azure.com/.default" }
        );

        private readonly BlobContainerClient blobContainerClient;
        private readonly TokenCredential credential;

        /// <summary>
        /// Creates a client for the configured container. Nothing is sent
        /// until the client is used.
        /// </summary>
        /// <exception cref="InvalidOperationException">The storage
        /// configuration is missing or invalid.</exception>
        public AzureBlobStorageClient()
        {
            (this.blobContainerClient, this.credential) = CreateBlobContainerClient();
        }

        /// <summary>
        /// Signs in now, if the client uses workload identity, so a problem
        /// is reported before the crawl rather than after it. The token is
        /// cached and reused by every upload. This matters because the
        /// GitHub OIDC token in AZURE_FEDERATED_TOKEN_FILE expires after a
        /// few minutes, while the Azure token it's exchanged for lasts
        /// about an hour. Does nothing for a connection string.
        /// </summary>
        /// <returns>An awaitable Task.</returns>
        /// <exception cref="InvalidOperationException">Signing in
        /// failed.</exception>
        public async Task SignIn()
        {
            if (this.credential == null)
                return;

            try
            {
                await this.credential.GetTokenAsync(StorageTokenRequest, CancellationToken.None);
            }
            catch (AuthenticationFailedException exception)
            {
                throw new InvalidOperationException(
                    $"Couldn't sign in to Azure Storage with workload identity: {exception.Message}",
                    exception
                );
            }
        }

        /// <summary>
        /// Uploads a blob, replacing any blob of the same name.
        /// </summary>
        /// <param name="blobName">The blob's name, e.g.
        /// snapshots/2026-11-10T09-15-00Z/site/index.html.</param>
        /// <param name="contents">The blob's contents.</param>
        /// <param name="contentType">The Content-Type the blob is served
        /// with.</param>
        /// <param name="cacheControl">The Cache-Control header the blob is
        /// served with, or null for none.</param>
        /// <returns>An awaitable Task.</returns>
        public async Task UploadBlob(
            string blobName,
            BinaryData contents,
            string contentType,
            string cacheControl = null
        )
        {
            await this.blobContainerClient
                .GetBlobClient(blobName)
                .UploadAsync(contents, CreateOptions(contentType, cacheControl, conditions: null));
        }

        /// <summary>
        /// Creates a blob, unless one of that name already exists.
        /// </summary>
        /// <param name="blobName">The blob's name.</param>
        /// <param name="contents">The blob's contents.</param>
        /// <param name="contentType">The Content-Type the blob is served
        /// with.</param>
        /// <returns>True if the blob was created, or false if it already
        /// existed and was left alone.</returns>
        public async Task<bool> TryCreateBlob(string blobName, BinaryData contents, string contentType)
        {
            BlobRequestConditions conditions = new() { IfNoneMatch = ETag.All };

            try
            {
                await this.blobContainerClient
                    .GetBlobClient(blobName)
                    .UploadAsync(contents, CreateOptions(contentType, null, conditions));
                return true;
            }
            catch (RequestFailedException exception) when (exception.Status == 409)
            {
                return false;
            }
        }

        /// <summary>
        /// Changes a blob based on its current contents, without losing a
        /// change made by someone else at the same time. The write only
        /// succeeds if the blob hasn't changed since it was read (or still
        /// doesn't exist), and is retried from a fresh read if it has.
        /// </summary>
        /// <param name="blobName">The blob's name.</param>
        /// <param name="update">Given the blob's current contents, or null
        /// if it doesn't exist, returns its new contents.</param>
        /// <param name="contentType">The Content-Type the blob is served
        /// with.</param>
        /// <param name="cacheControl">The Cache-Control header the blob is
        /// served with, or null for none.</param>
        /// <returns>An awaitable Task.</returns>
        /// <exception cref="InvalidOperationException">The blob kept
        /// changing, so it couldn't be updated.</exception>
        public async Task UpdateBlob(
            string blobName,
            Func<BinaryData, BinaryData> update,
            string contentType,
            string cacheControl = null
        )
        {
            BlobClient blobClient = this.blobContainerClient.GetBlobClient(blobName);

            for (int attempt = 1; attempt <= MaxUpdateAttempts; attempt++)
            {
                BinaryData current = null;
                BlobRequestConditions conditions;

                try
                {
                    BlobDownloadResult download = (await blobClient.DownloadContentAsync()).Value;
                    current = download.Content;
                    conditions = new BlobRequestConditions { IfMatch = download.Details.ETag };
                }
                catch (RequestFailedException exception) when (exception.Status == 404)
                {
                    conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
                }

                try
                {
                    await blobClient.UploadAsync(
                        update(current),
                        CreateOptions(contentType, cacheControl, conditions)
                    );
                    return;
                }
                catch (RequestFailedException exception) when (
                    // 412: changed since it was read. 409: created by
                    // someone else since it was found not to exist.
                    exception.Status == 412 || exception.Status == 409
                )
                {
                    Console.WriteLine($"{blobName} changed while it was being updated, retrying (attempt {attempt})");
                    await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(100, 500) * attempt));
                }
            }

            throw new InvalidOperationException(
                $"Couldn't update {blobName}, because it kept changing during {MaxUpdateAttempts} attempts"
            );
        }

        /// <summary>
        /// Builds the options for an upload.
        /// </summary>
        /// <param name="contentType">The blob's Content-Type.</param>
        /// <param name="cacheControl">The blob's Cache-Control, or
        /// null.</param>
        /// <param name="conditions">Conditions the write depends on, or
        /// null to always overwrite.</param>
        /// <returns>The upload options.</returns>
        private static BlobUploadOptions CreateOptions(
            string contentType,
            string cacheControl,
            BlobRequestConditions conditions
        )
        {
            return new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = contentType,
                    CacheControl = cacheControl,
                },
                Conditions = conditions,
            };
        }

        /// <summary>
        /// Creates the BlobContainerClient used for every request, from the
        /// environment variables.
        /// </summary>
        /// <returns>The container client, and the credential it signs in
        /// with, or null for a connection string.</returns>
        /// <exception cref="InvalidOperationException">The storage
        /// configuration is missing or invalid.</exception>
        private static (BlobContainerClient Client, TokenCredential Credential) CreateBlobContainerClient()
        {
            string connectionString = Read(ConnectionStringVariable);
            string endpoint = Read(EndpointVariable);
            string containerName = Read(ContainerNameVariable);

            if (containerName == null)
                throw new InvalidOperationException($"{ContainerNameVariable} isn't set");

            if (connectionString != null && endpoint != null)
            {
                throw new InvalidOperationException(
                    $"Both {ConnectionStringVariable} and {EndpointVariable} are set, so it isn't clear which " +
                    "storage account to use. Set only one"
                );
            }

            if (connectionString != null)
                return (new BlobServiceClient(connectionString).GetBlobContainerClient(containerName), null);

            if (endpoint == null)
            {
                throw new InvalidOperationException(
                    $"Neither {ConnectionStringVariable} nor {EndpointVariable} is set, so there's nowhere to " +
                    "upload the snapshot"
                );
            }

            // The SDK won't send a token over plain http
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri endpointUri) || endpointUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    $"{EndpointVariable} must be an https URL, such as https://<account>.blob.core.windows.net/, " +
                    $"but it's {endpoint}"
                );
            }

            string[] missing = WorkloadIdentityVariables.Where(name => Read(name) == null).ToArray();

            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"{EndpointVariable} is set, so the archiver signs in with workload identity, but " +
                    $"{string.Join(", ", missing)} {(missing.Length == 1 ? "isn't" : "aren't")} set"
                );
            }

            string tokenFile = Read(TokenFileVariable);

            if (!File.Exists(tokenFile))
                throw new InvalidOperationException($"{TokenFileVariable} is {tokenFile}, which doesn't exist");

            // Reads AZURE_TENANT_ID, AZURE_CLIENT_ID and
            // AZURE_FEDERATED_TOKEN_FILE. Chosen over DefaultAzureCredential
            // so it can't quietly sign in some other way.
            TokenCredential credential = new WorkloadIdentityCredential();

            return (new BlobServiceClient(endpointUri, credential).GetBlobContainerClient(containerName), credential);
        }

        /// <summary>
        /// Reads an environment variable.
        /// </summary>
        /// <param name="name">The variable's name.</param>
        /// <returns>Its value, or null if it's unset or blank.</returns>
        private static string Read(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
