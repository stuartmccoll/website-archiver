using System.Security.Cryptography;
using System.Text.Json;
using WebsiteArchiver.Tests.Fakes;
using static WebsiteArchiver.Tests.TestSites;

namespace WebsiteArchiver.Tests
{
    /// <summary>
    /// Tests how a snapshot is published: its manifest, unmodified
    /// originals, and its entry in the index.
    /// </summary>
    public class SnapshotTests
    {
        private const string IndexName = "snapshots/index.json";

        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF };

        private static void ConfigureSmallSite(FakeWebsite site) => site
            .WithPage("/", Page("Home", $"{D}posts/first", External))
            .WithRedirect("/posts/first", "/posts/first/")
            .WithPage("/posts/first/", Body("<img src=\"/img/photo.png\"><link rel=\"manifest\" href=\"/site.webmanifest\">"))
            .WithFile("/img/photo.png", Png, "image/png")
            .WithText("/site.webmanifest", "{ \"icons\": [] }", "application/manifest+json");

        private static string[] Strings(JsonElement array) =>
            array.EnumerateArray().Select(item => item.GetString()!).ToArray();

        [Fact]
        public async Task Manifest_DescribesTheSnapshot()
        {
            var (site, run) = await ArchiveAsync(ConfigureSmallSite);
            await using var _ = site;

            JsonElement manifest = run.Manifest;

            Assert.Equal(1, manifest.GetProperty("formatVersion").GetInt32());
            Assert.Equal(run.SnapshotId, manifest.GetProperty("id").GetString());
            Assert.Equal(site.Domain, manifest.GetProperty("domain").GetString());
            Assert.Equal(site.Domain, manifest.GetProperty("root").GetString());
            Assert.Equal("index.html", manifest.GetProperty("homepage").GetString());
            Assert.Equal("complete", manifest.GetProperty("status").GetString());
            Assert.Empty(manifest.GetProperty("failedAddresses").EnumerateArray());
            Assert.True(
                manifest.GetProperty("startedAt").GetDateTimeOffset() <=
                manifest.GetProperty("finishedAt").GetDateTimeOffset()
            );
            // A placeholder claims the folder first, and the real manifest
            // replaces it once every file is uploaded
            Assert.Equal(
                new[] { $"snapshots/{run.SnapshotId}/manifest.json", $"snapshots/{run.SnapshotId}/manifest.json" },
                run.Uploads.Where(name => name.EndsWith("/manifest.json"))
            );
            Assert.Equal($"snapshots/{run.SnapshotId}/manifest.json", run.Uploads[0]);
            Assert.Equal("application/json; charset=utf-8", run.AllBlobs[$"snapshots/{run.SnapshotId}/manifest.json"].ContentType);
        }

        [Fact]
        public async Task Manifest_ListsEveryFile()
        {
            var (site, run) = await ArchiveAsync(ConfigureSmallSite);
            await using var _ = site;

            JsonElement[] files = run.Manifest.GetProperty("files").EnumerateArray().ToArray();

            Assert.Equal(run.Paths, files.Select(file => file.GetProperty("path").GetString()).Order(StringComparer.Ordinal));

            JsonElement photo = files.Single(file => file.GetProperty("path").GetString() == "img/photo.png");
            Assert.Equal($"{site.Domain}img/photo.png", photo.GetProperty("url").GetString());
            Assert.Equal("image/png", photo.GetProperty("contentType").GetString());
            Assert.Equal(Png.Length, photo.GetProperty("size").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Png)), photo.GetProperty("sha256").GetString());
            Assert.False(photo.GetProperty("hasOriginal").GetBoolean());

            JsonElement post = files.Single(file => file.GetProperty("path").GetString() == "posts/first/index.html");
            Assert.Equal($"{site.Domain}posts/first/", post.GetProperty("url").GetString());
            Assert.True(post.GetProperty("hasOriginal").GetBoolean());
        }

        [Fact]
        public async Task Manifest_ListsRedirects()
        {
            var (site, run) = await ArchiveAsync(ConfigureSmallSite);
            await using var _ = site;

            JsonElement redirect = Assert.Single(run.Manifest.GetProperty("redirects").EnumerateArray());
            Assert.Equal($"{site.Domain}posts/first", redirect.GetProperty("from").GetString());
            Assert.Equal($"{site.Domain}posts/first/", redirect.GetProperty("to").GetString());
        }

        [Fact]
        public async Task HtmlCssAndWebManifests_HaveAnUnmodifiedOriginal()
        {
            var (site, run) = await ArchiveAsync(ConfigureSmallSite);
            await using var _ = site;

            Assert.Equal(
                new[] { "css/site.css", "index.html", "posts/first/index.html", "site.webmanifest" },
                run.Originals.Keys.Order(StringComparer.Ordinal)
            );

            foreach ((string path, FakeBlobStorage.StoredBlob original) in run.Originals)
            {
                Assert.Equal(run.Files[path].Contents, original.Contents);
                Assert.Equal(run.Files[path].ContentType, original.ContentType);
            }
        }

        [Fact]
        public async Task Index_ListsTheSnapshot()
        {
            var (site, run) = await ArchiveAsync(ConfigureSmallSite);
            await using var _ = site;

            Assert.Equal(1, run.Index.GetProperty("formatVersion").GetInt32());

            JsonElement entry = Assert.Single(run.Index.GetProperty("snapshots").EnumerateArray());
            Assert.Equal(run.SnapshotId, entry.GetProperty("id").GetString());
            Assert.Equal("complete", entry.GetProperty("status").GetString());
            Assert.Equal(site.Domain, entry.GetProperty("root").GetString());
            Assert.Equal("index.html", entry.GetProperty("homepage").GetString());
            Assert.Equal(2, entry.GetProperty("pageCount").GetInt32());
            Assert.Equal(5, entry.GetProperty("fileCount").GetInt32());
            Assert.Empty(entry.GetProperty("failedAddresses").EnumerateArray());
            Assert.Equal($"{run.SnapshotId}/manifest.json", entry.GetProperty("manifest").GetString());
            Assert.Equal(
                run.Manifest.GetProperty("startedAt").GetDateTimeOffset(),
                entry.GetProperty("startedAt").GetDateTimeOffset()
            );

            FakeBlobStorage.StoredBlob index = run.AllBlobs[IndexName];
            Assert.Equal("application/json; charset=utf-8", index.ContentType);
            Assert.Equal("no-cache", index.CacheControl);
        }

        [Fact]
        public async Task Index_IsWrittenLast_AfterTheManifest()
        {
            var (site, run) = await ArchiveAsync(ConfigureSmallSite);
            await using var _ = site;

            Assert.Equal(IndexName, run.Uploads[^1]);
            Assert.EndsWith("/manifest.json", run.Uploads[^2]);
            Assert.Single(run.Uploads, name => name == IndexName);
        }

        [Fact]
        public async Task RunWithFailures_IsPublishedAndMarkedIncomplete()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}missing/", $"{D}about/"))
                .WithPage("/about/", Page("About", External))
            );
            await using var _ = site;

            string[] expected = { $"{site.Domain}missing/" };

            Assert.Equal("incomplete", run.Manifest.GetProperty("status").GetString());
            Assert.Equal(expected, Strings(run.Manifest.GetProperty("failedAddresses")));
            Assert.Equal("incomplete", run.IndexEntry.GetProperty("status").GetString());
            Assert.Equal(expected, Strings(run.IndexEntry.GetProperty("failedAddresses")));
            Assert.Contains("about/index.html", run.Paths);
        }

        [Fact]
        public async Task ExistingIndex_KeepsItsEntriesAndUnknownProperties()
        {
            await using FakeBlobStorage storage = await FakeBlobStorage.StartAsync();
            storage.Seed(IndexName, """
                {
                  "formatVersion": 1,
                  "addedLater": true,
                  "snapshots": [ { "id": "2026-01-01T00-00-00Z", "addedLater": "kept" } ]
                }
                """);

            var (site, run) = await ArchiveAsync(ConfigureSmallSite, storage: storage);
            await using var _ = site;

            Assert.True(run.Index.GetProperty("addedLater").GetBoolean());
            JsonElement[] entries = run.Index.GetProperty("snapshots").EnumerateArray().ToArray();
            Assert.Equal(new[] { "2026-01-01T00-00-00Z", run.SnapshotId }, entries.Select(e => e.GetProperty("id").GetString()));
            Assert.Equal("kept", entries[0].GetProperty("addedLater").GetString());
        }

        [Fact]
        public async Task IndexChangedByAnotherRun_IsReadAgainSoNoEntryIsLost()
        {
            await using FakeBlobStorage storage = await FakeBlobStorage.StartAsync();
            storage.Seed(IndexName, """{ "formatVersion": 1, "snapshots": [ { "id": "first" } ] }""");
            storage.BeforeConditionalPut = OnceFor(IndexName, () =>
                storage.Seed(IndexName, """{ "formatVersion": 1, "snapshots": [ { "id": "first" }, { "id": "concurrent" } ] }""")
            );

            var (site, run) = await ArchiveAsync(ConfigureSmallSite, storage: storage);
            await using var _ = site;

            Assert.Equal(
                new[] { "first", "concurrent", run.SnapshotId },
                run.Index.GetProperty("snapshots").EnumerateArray().Select(e => e.GetProperty("id").GetString())
            );
        }

        [Fact]
        public async Task IndexCreatedByAnotherRun_IsReadAgainSoNoEntryIsLost()
        {
            await using FakeBlobStorage storage = await FakeBlobStorage.StartAsync();
            storage.BeforeConditionalPut = OnceFor(IndexName, () =>
                storage.Seed(IndexName, """{ "formatVersion": 1, "snapshots": [ { "id": "concurrent" } ] }""")
            );

            var (site, run) = await ArchiveAsync(ConfigureSmallSite, storage: storage);
            await using var _ = site;

            Assert.Equal(
                new[] { "concurrent", run.SnapshotId },
                run.Index.GetProperty("snapshots").EnumerateArray().Select(e => e.GetProperty("id").GetString())
            );
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("""{ "formatVersion": 1 }""")]
        [InlineData("[]")]
        public async Task UnreadableIndex_FailsTheRunAndIsLeftAlone(string contents)
        {
            await using FakeBlobStorage storage = await FakeBlobStorage.StartAsync();
            await using FakeWebsite site = await FakeWebsite.StartAsync();
            storage.Seed(IndexName, contents);
            site.WithPage("/", Body("<p>Home</p>"));

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ArchiveRun.ExecuteAsync(site, storage: storage)
            );

            Assert.Contains(IndexName, exception.Message);
            Assert.Equal(contents, storage.Blobs[IndexName].Text);
        }

        [Fact]
        public async Task RunStartedInTheSameSecondAsAnother_GetsItsOwnFolder()
        {
            await using FakeBlobStorage storage = await FakeBlobStorage.StartAsync();

            // As if another run had started in each of the next few seconds
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string[] taken = Enumerable.Range(0, 5)
                .Select(seconds => Snapshot.CreateId(now.AddSeconds(seconds)))
                .ToArray();

            foreach (string id in taken)
                storage.Seed($"snapshots/{id}/manifest.json", "{ \"other\": true }");

            var (site, run) = await ArchiveAsync(ConfigureSmallSite, storage: storage);
            await using var _ = site;

            Assert.Matches(@"Z-2$", run.SnapshotId);
            Assert.Contains(run.SnapshotId.Substring(0, run.SnapshotId.Length - 2), taken);
            Assert.Equal(run.SnapshotId, run.IndexEntry.GetProperty("id").GetString());
            Assert.Equal(run.SnapshotId, run.Manifest.GetProperty("id").GetString());

            foreach (string id in taken)
                Assert.Equal("{ \"other\": true }", storage.Blobs[$"snapshots/{id}/manifest.json"].Text);
        }

        /// <summary>
        /// Runs an action the first time a conditional write to the given
        /// blob arrives, as if another run had written it just before.
        /// </summary>
        private static Action<string> OnceFor(string blobName, Action action)
        {
            bool done = false;

            return name =>
            {
                if (name == blobName && !done)
                {
                    done = true;
                    action();
                }
            };
        }
    }
}
