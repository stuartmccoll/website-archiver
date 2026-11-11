using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace WebsiteArchiver
{
    /// <summary>
    /// What kind of file was captured, which decides whether it has links
    /// to rewrite.
    /// </summary>
    public enum CapturedFormat
    {
        /// <summary>Any other file, which is stored as it is.</summary>
        Other,

        /// <summary>An HTML page.</summary>
        Html,

        /// <summary>A stylesheet.</summary>
        Css,

        /// <summary>A web app manifest.</summary>
        WebManifest,
    }

    /// <summary>
    /// A file fetched during a crawl, held in memory until the snapshot is
    /// published.
    /// </summary>
    /// <param name="Address">The address the file was served from, after
    /// any redirects.</param>
    /// <param name="Path">Where the file goes inside the snapshot, from
    /// <see cref="SnapshotPaths.FromAddress"/>.</param>
    /// <param name="Content">The response body, byte for byte.</param>
    /// <param name="ContentType">The response's Content-Type.</param>
    /// <param name="Format">What kind of file it is.</param>
    public record CapturedFile(
        Uri Address,
        string Path,
        byte[] Content,
        string ContentType,
        CapturedFormat Format
    )
    {
        /// <summary>
        /// Whether a copy also goes in original/. True for the HTML, CSS
        /// and web manifests that links are rewritten in.
        /// </summary>
        public bool KeepOriginal => this.Format != CapturedFormat.Other;

        /// <summary>
        /// The copy that goes in site/, with its links rewritten, or null
        /// if it's the same as <see cref="Content"/>.
        /// </summary>
        public byte[] SiteContent { get; init; }

        /// <summary>
        /// The Content-Type of <see cref="SiteContent"/>, or null if it's
        /// the same as <see cref="ContentType"/>.
        /// </summary>
        public string SiteContentType { get; init; }
    }

    /// <summary>
    /// A redirect followed during a crawl, e.g. from /post to /post/.
    /// </summary>
    /// <param name="From">The address that was requested.</param>
    /// <param name="To">The address it redirected to.</param>
    public record SnapshotRedirect(string From, string To);

    /// <summary>
    /// A file listed in a snapshot's manifest.json.
    /// </summary>
    /// <param name="Url">The address the file was served from.</param>
    /// <param name="Path">Its path inside site/ (and original/, if it has
    /// one).</param>
    /// <param name="ContentType">The Content-Type it was served
    /// with.</param>
    /// <param name="Size">Its size in bytes, as served.</param>
    /// <param name="Sha256">The SHA-256 hash of its contents as served,
    /// in lowercase hex. For a file with an original, that's the
    /// original's, not the rewritten copy's.</param>
    /// <param name="HasOriginal">Whether an unmodified copy is kept in
    /// original/.</param>
    public record SnapshotFile(
        string Url,
        string Path,
        string ContentType,
        long Size,
        string Sha256,
        bool HasOriginal
    );

    /// <summary>
    /// The contents of a snapshot's manifest.json.
    /// </summary>
    public class SnapshotManifest
    {
        /// <summary>The version of this format.</summary>
        public int FormatVersion { get; init; } = Snapshot.FormatVersion;

        /// <summary>The snapshot's ID, which is also its folder name, e.g.
        /// 2026-11-10T09-15-00Z, or 2026-11-10T09-15-00Z-2 if another run
        /// had already claimed that folder.</summary>
        public string Id { get; set; }

        /// <summary>The DOMAIN the crawl started from.</summary>
        public string Domain { get; init; }

        /// <summary>The site root, after following any redirect from
        /// DOMAIN. Pages are only crawled beneath it.</summary>
        public string Root { get; init; }

        /// <summary>The homepage's path inside the snapshot.</summary>
        public string Homepage { get; init; }

        /// <summary>When the crawl started, in UTC.</summary>
        public DateTimeOffset StartedAt { get; init; }

        /// <summary>When the crawl finished, in UTC.</summary>
        public DateTimeOffset FinishedAt { get; init; }

        /// <summary>"complete", or "incomplete" if anything couldn't be
        /// fetched.</summary>
        public string Status { get; init; }

        /// <summary>Addresses that couldn't be fetched.</summary>
        public IReadOnlyList<string> FailedAddresses { get; init; }

        /// <summary>Redirects followed during the crawl.</summary>
        public IReadOnlyList<SnapshotRedirect> Redirects { get; init; }

        /// <summary>Every file in the snapshot.</summary>
        public IReadOnlyList<SnapshotFile> Files { get; init; }
    }

    /// <summary>
    /// Names and publishes snapshots. Each snapshot is stored as:
    /// <code>
    /// snapshots/index.json                 every published snapshot
    /// snapshots/&lt;id&gt;/manifest.json        what this snapshot contains
    /// snapshots/&lt;id&gt;/site/&lt;path&gt;          every file
    /// snapshots/&lt;id&gt;/original/&lt;path&gt;      HTML, CSS and web manifests, unmodified
    /// </code>
    /// </summary>
    public static class Snapshot
    {
        /// <summary>The version of the manifest and index formats.</summary>
        public const int FormatVersion = 1;

        /// <summary>The folder every snapshot is stored in.</summary>
        public const string Folder = "snapshots";

        /// <summary>The name of the index of published snapshots.</summary>
        public const string IndexBlobName = Folder + "/index.json";

        private const string JsonContentType = "application/json; charset=utf-8";
        private const int MaxParallelUploads = 8;
        private const int MaxIdAttempts = 100;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            // The JSON is served on its own, never embedded in HTML, so
            // characters such as & needn't be escaped
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// Builds a snapshot's ID from the time its crawl started, e.g.
        /// 2026-11-10T09-15-00Z. IDs sort in date order, and are safe to
        /// use in a URL.
        /// </summary>
        /// <param name="startedAt">When the crawl started.</param>
        /// <returns>The snapshot's ID.</returns>
        public static string CreateId(DateTimeOffset startedAt)
        {
            return startedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH-mm-ss'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Lists a captured file in the manifest.
        /// </summary>
        /// <param name="file">A captured file.</param>
        /// <returns>The file's manifest entry.</returns>
        public static SnapshotFile Describe(CapturedFile file)
        {
            return new SnapshotFile(
                file.Address.AbsoluteUri,
                file.Path,
                file.ContentType,
                file.Content.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(file.Content)),
                file.KeepOriginal
            );
        }

        /// <summary>
        /// Claims a folder for the snapshot, uploads its files, then its
        /// manifest, then adds it to the index. The index is written last,
        /// so a snapshot that couldn't be fully uploaded is never listed.
        /// </summary>
        /// <param name="storage">Where to upload the snapshot.</param>
        /// <param name="manifest">The snapshot's manifest. Its ID is
        /// changed if another run already has a folder of that
        /// name.</param>
        /// <param name="files">The snapshot's files.</param>
        /// <returns>The snapshot's ID.</returns>
        internal static async Task<string> Publish(
            AzureBlobStorageClient storage,
            SnapshotManifest manifest,
            IReadOnlyList<CapturedFile> files
        )
        {
            await ClaimId(storage, manifest);

            string prefix = $"{Folder}/{manifest.Id}/";

            Console.WriteLine($"Uploading {files.Count} file(s) to {prefix}");

            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelUploads },
                async (file, cancellationToken) =>
                {
                    await storage.UploadBlob(
                        $"{prefix}site/{file.Path}",
                        BinaryData.FromBytes(file.SiteContent ?? file.Content),
                        file.SiteContentType ?? file.ContentType
                    );

                    if (file.KeepOriginal)
                    {
                        await storage.UploadBlob(
                            $"{prefix}original/{file.Path}",
                            BinaryData.FromBytes(file.Content),
                            file.ContentType
                        );
                    }
                }
            );

            await storage.UploadBlob(
                $"{prefix}manifest.json",
                BinaryData.FromString(JsonSerializer.Serialize(manifest, JsonOptions)),
                JsonContentType
            );

            Console.WriteLine($"Adding {manifest.Id} to {IndexBlobName}");

            // no-cache, so the viewer always sees the latest snapshots
            await storage.UpdateBlob(
                IndexBlobName,
                current => AddToIndex(current, manifest),
                JsonContentType,
                "no-cache"
            );

            return manifest.Id;
        }

        /// <summary>
        /// Claims the snapshot's folder by creating its manifest.json, as a
        /// placeholder until the real one is uploaded, only if it doesn't
        /// already exist. If another run started in the same second and
        /// took the folder first, -2, -3 and so on are added to the ID
        /// until one is free, so IDs still sort in date order.
        /// </summary>
        /// <param name="storage">Where the snapshot is uploaded.</param>
        /// <param name="manifest">The snapshot's manifest, whose ID is
        /// changed to the one claimed.</param>
        /// <returns>An awaitable Task.</returns>
        /// <exception cref="InvalidOperationException">No free ID was
        /// found.</exception>
        private static async Task ClaimId(AzureBlobStorageClient storage, SnapshotManifest manifest)
        {
            string baseId = manifest.Id;

            for (int attempt = 1; attempt <= MaxIdAttempts; attempt++)
            {
                string id = attempt == 1 ? baseId : $"{baseId}-{attempt}";
                string placeholder = JsonSerializer.Serialize(new { Id = id, Status = "uploading" }, JsonOptions);

                if (await storage.TryCreateBlob($"{Folder}/{id}/manifest.json", BinaryData.FromString(placeholder), JsonContentType))
                {
                    if (id != baseId)
                        Console.WriteLine($"Snapshot {baseId} already exists, so publishing as {id}");

                    manifest.Id = id;
                    return;
                }
            }

            throw new InvalidOperationException($"Couldn't find a free snapshot ID starting {baseId}");
        }

        /// <summary>
        /// Adds a snapshot to the index, keeping every existing entry and
        /// any properties this version doesn't know about.
        /// </summary>
        /// <param name="current">The index's current contents, or null if
        /// there isn't one yet.</param>
        /// <param name="manifest">The snapshot to add.</param>
        /// <returns>The index's new contents.</returns>
        /// <exception cref="InvalidOperationException">The existing index
        /// isn't in the expected format, so it's left alone rather than
        /// overwritten.</exception>
        private static BinaryData AddToIndex(BinaryData current, SnapshotManifest manifest)
        {
            JsonObject index;

            if (current == null)
            {
                index = new JsonObject
                {
                    ["formatVersion"] = FormatVersion,
                    ["snapshots"] = new JsonArray(),
                };
            }
            else
            {
                try
                {
                    index = JsonNode.Parse(current.ToString()) as JsonObject;
                }
                catch (JsonException)
                {
                    index = null;
                }
            }

            if (index?["snapshots"] is not JsonArray snapshots)
            {
                throw new InvalidOperationException(
                    $"{IndexBlobName} doesn't contain a snapshots list, so snapshot {manifest.Id} wasn't added to it"
                );
            }

            snapshots.Add(JsonSerializer.SerializeToNode(CreateIndexEntry(manifest), JsonOptions));

            return BinaryData.FromString(index.ToJsonString(JsonOptions));
        }

        /// <summary>
        /// Summarises a snapshot for the index.
        /// </summary>
        /// <param name="manifest">The snapshot's manifest.</param>
        /// <returns>The snapshot's index entry.</returns>
        private static object CreateIndexEntry(SnapshotManifest manifest)
        {
            return new
            {
                manifest.Id,
                manifest.StartedAt,
                manifest.FinishedAt,
                manifest.Root,
                manifest.Homepage,
                manifest.Status,
                PageCount = manifest.Files.Count(file => IsHtml(file.ContentType)),
                FileCount = manifest.Files.Count,
                manifest.FailedAddresses,
                Manifest = $"{manifest.Id}/manifest.json",
            };
        }

        /// <summary>
        /// Determines whether a Content-Type is HTML.
        /// </summary>
        /// <param name="contentType">A Content-Type header value, or
        /// null.</param>
        /// <returns>True for text/html and application/xhtml+xml.</returns>
        public static bool IsHtml(string contentType)
        {
            string mediaType = MediaType(contentType);
            return mediaType == "text/html" || mediaType == "application/xhtml+xml";
        }

        /// <summary>
        /// Gets the media type from a Content-Type, without parameters such
        /// as charset, in lowercase.
        /// </summary>
        /// <param name="contentType">A Content-Type header value, or
        /// null.</param>
        /// <returns>The media type, e.g. text/css, or an empty
        /// string.</returns>
        public static string MediaType(string contentType)
        {
            return (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        }
    }
}
