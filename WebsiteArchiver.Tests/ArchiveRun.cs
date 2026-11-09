using System.Globalization;
using WebsiteArchiver.Tests.Fakes;

namespace WebsiteArchiver.Tests
{
    /// <summary>
    /// Runs the real <see cref="Archiver"/> end to end against a
    /// <see cref="FakeWebsite"/> and a <see cref="FakeBlobStorage"/>, and
    /// exposes what was uploaded.
    /// </summary>
    public sealed class ArchiveRun
    {
        private ArchiveRun(
            string datePrefix,
            IReadOnlyList<(string Name, string Contents)> uploads,
            IReadOnlyList<string> requestedPaths,
            IReadOnlyList<string> failedAddresses
        )
        {
            this.FailedAddresses = failedAddresses;
            this.DatePrefix = datePrefix;
            this.Uploads = uploads;
            this.RequestedPaths = requestedPaths;
        }

        /// <summary>
        /// The ddMMyyyy folder every blob in this run was uploaded under.
        /// </summary>
        public string DatePrefix { get; }

        /// <summary>
        /// Every upload in order, with the date prefix removed from the name.
        /// </summary>
        public IReadOnlyList<(string Name, string Contents)> Uploads { get; }

        /// <summary>
        /// Every path the archiver requested from the website, in order.
        /// </summary>
        public IReadOnlyList<string> RequestedPaths { get; }

        /// <summary>
        /// Addresses the archiver reported it couldn't fetch.
        /// </summary>
        public IReadOnlyList<string> FailedAddresses { get; }

        /// <summary>
        /// Final contents of each blob, keyed by name without the date prefix.
        /// </summary>
        public IReadOnlyDictionary<string, string> Blobs =>
            this.Uploads
                .GroupBy(upload => upload.Name)
                .ToDictionary(group => group.Key, group => group.Last().Contents);

        /// <param name="site">The website to archive.</param>
        /// <param name="domain">Overrides the DOMAIN environment variable,
        /// which otherwise defaults to the site's root URL.</param>
        public static async Task<ArchiveRun> ExecuteAsync(FakeWebsite site, string? domain = null)
        {
            await using FakeBlobStorage storage = await FakeBlobStorage.StartAsync();

            Environment.SetEnvironmentVariable("DOMAIN", domain ?? site.Domain);
            Environment.SetEnvironmentVariable(
                "AZURE_BLOB_STORAGE_CONNECTION_STRING",
                storage.ConnectionString
            );
            Environment.SetEnvironmentVariable(
                "AZURE_BLOB_STORAGE_CONTAINER_NAME",
                FakeBlobStorage.ContainerName
            );

            string dateBefore = Today();
            Archiver archiver = new();
            await archiver.Crawl();
            string dateAfter = Today();

            Assert.Empty(storage.UnexpectedRequests);

            // The archiver uses DateTime.Now, so allow for a run that
            // crosses midnight.
            string datePrefix = storage.Uploads.Count > 0
                ? storage.Uploads[0].Name.Split('/')[0]
                : dateBefore;
            Assert.Contains(datePrefix, new[] { dateBefore, dateAfter });

            List<(string Name, string Contents)> uploads = new();
            foreach ((string name, string contents) in storage.Uploads)
            {
                Assert.StartsWith(datePrefix + "/", name);
                uploads.Add((name.Substring(datePrefix.Length + 1), contents));
            }

            return new ArchiveRun(datePrefix, uploads, site.RequestedPaths, archiver.FailedAddresses);
        }

        private static string Today() =>
            DateTime.Now.ToString("ddMMyyyy", CultureInfo.InvariantCulture);
    }
}
