using Azure.Storage.Blobs;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace WebsiteArchiver
{
    class AzureBlobStorageClient
    {
        public async Task UploadBlob(String fileName, String fileContents)
        {
            String connection = "";
            String containerName = "archives";

            BlobServiceClient serviceClient = new BlobServiceClient(connection);

            BlobContainerClient blobContainerClient = serviceClient.GetBlobContainerClient(containerName);

            Console.WriteLine($"Uploading {fileName} to Azure Blob Storage");

            string path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            fileName = $"{fileName}.html";

            String localFile = Path.Combine(path, fileName);

            await File.WriteAllTextAsync(localFile, fileContents);

            string nameForBlob = this.getNameForNewBlob(fileName);

            BlobClient blobClient = blobContainerClient.GetBlobClient(nameForBlob);

            Console.WriteLine($"Uploading to Azure Blob Storage as blob:\n\t {blobClient.Uri}");

            await blobClient.UploadAsync(localFile, true);

            this.RemoveLocalFile(localFile);
        }

        private string getNameForNewBlob(string fileNameOnSystem)
        {
            // TODO: Parse through any nested structure (such as 'posts/')

            string currentDate = DateTime.Now.ToString("ddMMyyyy");

            return $"{currentDate}/{fileNameOnSystem}";
        }

        private void RemoveLocalFile(String filePath)
        {
            Console.WriteLine($"Removing local file at location: {filePath}");
            File.Delete(filePath);
        }
    }
}
