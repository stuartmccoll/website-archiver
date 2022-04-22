using Azure.Storage.Blobs;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace WebsiteArchiver
{
    class AzureBlobStorageClient
    {
        private readonly BlobContainerClient blobContainerClient;

        public AzureBlobStorageClient()
        {
            blobContainerClient = GetBlobContainerClient();
        }

        /// <summary>
        /// Uploads a file to Azure Blob Storage.
        /// </summary>
        /// <param name="fileName">The name of the file.</param>
        /// <param name="fileExtension">The extension of the file.</param>
        /// <param name="fileContents">The contents of the file.</param>
        /// <returns>An awaitable Task.</returns>
        public async Task UploadBlob(
            String fileName,
            String fileExtension,
            String fileContents
        )
        {
            Console.WriteLine($"Uploading {fileName} to Azure Blob Storage");

            string directory = this.GetFileDirectory(fileName);

            fileName = this.FormatFileName(fileName, fileExtension);

            string localFilePath = this.GetLocalFilePath(fileName);

            await File.WriteAllTextAsync(localFilePath, fileContents);

            if (directory.Length > 0)
                fileName = $"{directory}/{fileName}";

            string nameForBlob = this.GetNameForNewBlob(fileName);

            BlobClient blobClient = this.blobContainerClient.GetBlobClient(nameForBlob);

            Console.WriteLine($"Uploading to Azure Blob Storage as blob:\n\t {blobClient.Uri}");

            await blobClient.UploadAsync(localFilePath, true);

            this.RemoveLocalFile(localFilePath);
        }

        /// <summary>
        /// Initialises an instance of a BlobConntainerClient, used in later
        /// calls to Azure Blob Storage.
        /// </summary>
        /// <returns>A configured BlobContainerClient.</returns>
        private BlobContainerClient GetBlobContainerClient()
        {
            String secret = Environment.GetEnvironmentVariable(
                "AZURE_BLOB_STORAGE_CONNECTION_STRING"
            );

            Console.WriteLine($"Secret is: {secret}");

            String connection = $"DefaultEndpointsProtocol=https;AccountName=websitearchiver;AccountKey={secret};EndpointSuffix=core.windows.net";

            Console.WriteLine($"Full connection string is: {connection}");

            String containerName = Environment.GetEnvironmentVariable(
                "AZURE_BLOB_STORAGE_CONTAINER_NAME"
            );

            BlobServiceClient serviceClient = new BlobServiceClient(connection);

            return serviceClient.GetBlobContainerClient(containerName);
        }

        /// <summary>
        /// Gets the full path to a local file.
        /// </summary>
        /// <param name="fileName">The name of a file.</param>
        /// <returns>The full path to a local file.</returns>
        private String GetLocalFilePath(string fileName)
        {
            string path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(path, fileName);
        }

        /// <summary>
        /// Gets the directory for a file, by reading from the start of the
        /// file name up to the first / character (if one exists).
        /// </summary>
        /// <param name="fileName">The name of a file.</param>
        /// <returns>The directory for a file, if one exists.</returns>
        private String GetFileDirectory(string fileName)
        {
            string directory = "";
            if (fileName.IndexOf("/") > 0)
                directory = fileName.Substring(0, fileName.IndexOf("/"));
            return directory;
        }

        /// <summary>
        /// Formats a file name by:
        /// <list type="bullet">
        /// <item>
        /// <description>
        /// Appending the file extension if one exists.
        /// </description>
        /// </item>
        /// <item>
        /// <description>
        /// Replacing any / characters with - characters.
        /// </description>
        /// </item>
        /// </list>
        /// </summary>
        /// <param name="fileName">The name of a file.</param>
        /// <param name="fileExtension">The extension for a file.</param>
        /// <returns>A formatted file name, with the file extension added if
        /// applicable, and any / characters replaced with - characters.</returns>
        private String FormatFileName(string fileName, string fileExtension)
        {
            fileName = fileExtension.Length > 0 ? $"{fileName}.{fileExtension}" : fileName;

            if (fileName.IndexOf("/") > 0)
            {
                fileName = fileName.Remove(
                    0,
                    fileName.IndexOf("/") + 1
                ).Replace("/", "-");
            }

            return fileName;
        }

        /// <summary>
        /// Constructs a name for a new blob by prepending the desired
        /// filename with the current date. This avoids clashes/overwrites
        /// of existing blobs in Azure Blob Storage.
        /// </summary>
        /// <param name="fileNameOnSystem">The existing file name.</param>
        /// <returns>The existing file name prepended with the
        /// current date.</returns>
        private string GetNameForNewBlob(string fileNameOnSystem)
        {
            string currentDate = DateTime.Now.ToString("ddMMyyyy");
            return $"{currentDate}/{fileNameOnSystem}";
        }

        /// <summary>
        /// Removes a file from local storage.
        /// </summary>
        /// <param name="filePath">Path to the file being removed.</param>
        private void RemoveLocalFile(String filePath)
        {
            Console.WriteLine($"Removing local file at location: {filePath}");
            File.Delete(filePath);
        }
    }
}
