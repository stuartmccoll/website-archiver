using System;
using System.IO;
using System.Threading.Tasks;

namespace WebsiteArchiver
{
    /// <summary>
    /// The snapshot viewer, viewer/index.html. It's embedded in the app and
    /// uploaded on every run, so the published viewer always matches the
    /// snapshot format, with no separate deployment.
    /// </summary>
    public static class Viewer
    {
        /// <summary>
        /// The viewer's blob name. It's at the root of the container, next
        /// to snapshots/, so static website hosting serves it as the front
        /// page, from the same address as the snapshots.
        /// </summary>
        public const string BlobName = "index.html";

        private const string ResourceName = "WebsiteArchiver.viewer.index.html";

        /// <summary>
        /// Uploads the viewer, replacing any older version. It's served with
        /// Cache-Control: no-cache, so a new version is picked up straight
        /// away.
        /// </summary>
        /// <param name="storage">Where to upload the viewer.</param>
        /// <returns>An awaitable Task.</returns>
        internal static async Task Publish(AzureBlobStorageClient storage)
        {
            Console.WriteLine($"Uploading the viewer to {BlobName}");

            await storage.UploadBlob(
                BlobName,
                BinaryData.FromBytes(Load()),
                "text/html; charset=utf-8",
                "no-cache"
            );
        }

        /// <summary>
        /// Reads the viewer from the app's embedded resources.
        /// </summary>
        /// <returns>The viewer's contents, byte for byte.</returns>
        /// <exception cref="InvalidOperationException">The viewer isn't
        /// embedded, which means the build is broken.</exception>
        private static byte[] Load()
        {
            using Stream stream = typeof(Viewer).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"The viewer isn't embedded in the app as {ResourceName}");
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
