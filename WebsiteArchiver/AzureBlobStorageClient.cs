using System;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace WebsiteArchiver
{
    /// <summary>
    /// Uploads to the blob container named by
    /// AZURE_BLOB_STORAGE_CONTAINER_NAME, in the account given by
    /// AZURE_BLOB_STORAGE_CONNECTION_STRING.
    /// </summary>
    class AzureBlobStorageClient
    {
        private const int MaxUpdateAttempts = 10;

        private readonly BlobContainerClient blobContainerClient;

        /// <summary>
        /// Creates a client for the configured container.
        /// </summary>
        public AzureBlobStorageClient()
        {
            this.blobContainerClient = GetBlobContainerClient();
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
        /// Initialises the BlobContainerClient used for every request.
        /// </summary>
        /// <returns>A configured BlobContainerClient.</returns>
        private static BlobContainerClient GetBlobContainerClient()
        {
            string connection = Environment.GetEnvironmentVariable(
                "AZURE_BLOB_STORAGE_CONNECTION_STRING"
            );
            string containerName = Environment.GetEnvironmentVariable(
                "AZURE_BLOB_STORAGE_CONTAINER_NAME"
            );

            BlobServiceClient serviceClient = new BlobServiceClient(connection);

            return serviceClient.GetBlobContainerClient(containerName);
        }
    }
}
