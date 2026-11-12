using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebsiteArchiver.Tests.Fakes;
using StoredBlob = WebsiteArchiver.Tests.Fakes.FakeBlobStorage.StoredBlob;

namespace WebsiteArchiver.Tests
{
    /// <summary>
    /// Runs the real <see cref="Archiver"/> end to end against a
    /// <see cref="FakeWebsite"/> and a <see cref="FakeBlobStorage"/>, and
    /// exposes the snapshot it published.
    /// </summary>
    public sealed class ArchiveRun
    {
        private ArchiveRun(
            string snapshotId,
            IReadOnlyDictionary<string, StoredBlob> blobs,
            IReadOnlyList<string> uploads,
            IReadOnlyList<string> requestedPaths,
            IReadOnlyList<string> failedAddresses
        )
        {
            string prefix = $"snapshots/{snapshotId}/";

            this.SnapshotId = snapshotId;
            this.AllBlobs = blobs;
            this.Uploads = uploads;
            this.RequestedPaths = requestedPaths;
            this.FailedAddresses = failedAddresses;
            this.Files = WithPrefix(blobs, prefix + "site/");
            this.Originals = WithPrefix(blobs, prefix + "original/");
            this.Manifest = JsonDocument.Parse(blobs[prefix + "manifest.json"].Contents).RootElement;
            this.Index = JsonDocument.Parse(blobs["snapshots/index.json"].Contents).RootElement;
        }

        /// <summary>
        /// The ID, and folder name, of the snapshot this run published.
        /// </summary>
        public string SnapshotId { get; }

        /// <summary>
        /// Every blob in storage after the run, by full name.
        /// </summary>
        public IReadOnlyDictionary<string, StoredBlob> AllBlobs { get; }

        /// <summary>
        /// The full name of every blob uploaded during the run, in the
        /// order the uploads completed.
        /// </summary>
        public IReadOnlyList<string> Uploads { get; }

        /// <summary>
        /// The snapshot's site/ files, keyed by their path inside it, e.g.
        /// about/index.html.
        /// </summary>
        public IReadOnlyDictionary<string, StoredBlob> Files { get; }

        /// <summary>
        /// The snapshot's original/ files, keyed by their path inside it.
        /// </summary>
        public IReadOnlyDictionary<string, StoredBlob> Originals { get; }

        /// <summary>
        /// The snapshot's manifest.json.
        /// </summary>
        public JsonElement Manifest { get; }

        /// <summary>
        /// snapshots/index.json after the run.
        /// </summary>
        public JsonElement Index { get; }

        /// <summary>
        /// Every path the archiver requested from the website, in order.
        /// </summary>
        public IReadOnlyList<string> RequestedPaths { get; }

        /// <summary>
        /// Addresses the archiver reported it couldn't fetch.
        /// </summary>
        public IReadOnlyList<string> FailedAddresses { get; }

        /// <summary>
        /// The paths of the snapshot's site/ files, in ordinal order.
        /// </summary>
        public IEnumerable<string> Paths => this.Files.Keys.Order(StringComparer.Ordinal);

        /// <summary>
        /// A site/ file's contents, decoded as UTF-8.
        /// </summary>
        public string Text(string path) => this.Files[path].Text;

        /// <summary>
        /// An original/ file's contents, decoded as UTF-8. This is the file
        /// exactly as it was served, before its links were rewritten.
        /// </summary>
        public string OriginalText(string path) => this.Originals[path].Text;

        /// <summary>
        /// This run's entry in snapshots/index.json.
        /// </summary>
        public JsonElement IndexEntry =>
            this.Index.GetProperty("snapshots")
                .EnumerateArray()
                .Single(entry => entry.GetProperty("id").GetString() == this.SnapshotId);

        /// <param name="site">The website to archive.</param>
        /// <param name="domain">Overrides the DOMAIN environment variable,
        /// which otherwise defaults to the site's root URL.</param>
        /// <param name="storage">Storage to publish to, for tests that set
        /// it up first. Otherwise a new, empty one is used.</param>
        public static async Task<ArchiveRun> ExecuteAsync(
            FakeWebsite site,
            string? domain = null,
            FakeBlobStorage? storage = null
        )
        {
            FakeBlobStorage ownStorage = storage ?? await FakeBlobStorage.StartAsync();

            try
            {
                Environment.SetEnvironmentVariable("DOMAIN", domain ?? site.Domain);
                Environment.SetEnvironmentVariable(
                    "AZURE_BLOB_STORAGE_CONNECTION_STRING",
                    ownStorage.ConnectionString
                );
                Environment.SetEnvironmentVariable(
                    "AZURE_BLOB_STORAGE_CONTAINER_NAME",
                    FakeBlobStorage.ContainerName
                );

                DateTime before = Truncate(DateTime.UtcNow);
                Archiver archiver = new();
                await archiver.Crawl();
                DateTime after = DateTime.UtcNow;

                Assert.Empty(ownStorage.UnexpectedRequests);

                // An ID may have -2 or similar added if it was already taken
                DateTime snapshotTime = DateTime.ParseExact(
                    Regex.Replace(archiver.SnapshotId, @"Z-\d+$", "Z"),
                    "yyyy-MM-dd'T'HH-mm-ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal
                );
                Assert.InRange(snapshotTime, before, after);

                IReadOnlyList<string> uploads = ownStorage.Uploads;
                Assert.All(
                    uploads.Where(name => name != "snapshots/index.json" && name != Viewer.BlobName),
                    name => Assert.StartsWith($"snapshots/{archiver.SnapshotId}/", name)
                );

                return new ArchiveRun(
                    archiver.SnapshotId,
                    ownStorage.Blobs,
                    uploads,
                    site.RequestedPaths,
                    archiver.FailedAddresses
                );
            }
            finally
            {
                if (storage == null)
                    await ownStorage.DisposeAsync();
            }
        }

        private static DateTime Truncate(DateTime time) =>
            new(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        private static IReadOnlyDictionary<string, StoredBlob> WithPrefix(
            IReadOnlyDictionary<string, StoredBlob> blobs,
            string prefix
        ) =>
            blobs
                .Where(blob => blob.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(blob => blob.Key.Substring(prefix.Length), blob => blob.Value);
    }
}
