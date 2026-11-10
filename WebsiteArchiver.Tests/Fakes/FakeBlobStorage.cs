using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace WebsiteArchiver.Tests.Fakes
{
    /// <summary>
    /// A minimal stand-in for the Azure Blob Storage REST API. It supports
    /// Put Blob (which is all the archiver issues for small files), with
    /// If-Match and If-None-Match conditions, and Get Blob. It records each
    /// blob's contents, Content-Type and Cache-Control. Authentication is
    /// not checked.
    /// </summary>
    public sealed class FakeBlobStorage : IAsyncDisposable
    {
        public const string AccountName = "devstoreaccount1";
        public const string ContainerName = "archive";

        private readonly WebApplication app;
        private readonly object blobsLock = new();
        private readonly Dictionary<string, StoredBlob> blobs = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> uploads = new();
        private readonly ConcurrentQueue<string> unexpectedRequests = new();
        private string endpoint = "";

        private FakeBlobStorage(WebApplication app)
        {
            this.app = app;
        }

        /// <summary>
        /// A stored blob.
        /// </summary>
        public sealed record StoredBlob(byte[] Contents, string? ContentType, string? CacheControl, string ETag)
        {
            public string Text => Encoding.UTF8.GetString(this.Contents);
        }

        /// <summary>
        /// The current contents of every blob, by name.
        /// </summary>
        public IReadOnlyDictionary<string, StoredBlob> Blobs
        {
            get
            {
                lock (this.blobsLock)
                {
                    return new Dictionary<string, StoredBlob>(this.blobs);
                }
            }
        }

        /// <summary>
        /// The name of every blob uploaded through the API, in the order
        /// the uploads completed, including repeat uploads of the same
        /// name. Blobs added with <see cref="Seed"/> aren't included.
        /// </summary>
        public IReadOnlyList<string> Uploads => this.uploads.ToList();

        /// <summary>
        /// Any request other than a Put Blob or Get Blob in the expected
        /// container.
        /// </summary>
        public IReadOnlyList<string> UnexpectedRequests => this.unexpectedRequests.ToList();

        /// <summary>
        /// Called with the blob's name just before a conditional Put Blob
        /// is checked, so a test can change the blob as if another writer
        /// got there first.
        /// </summary>
        public Action<string>? BeforeConditionalPut { get; set; }

        /// <summary>
        /// A connection string that points the Azure SDK at this fake.
        /// </summary>
        public string ConnectionString =>
            "DefaultEndpointsProtocol=http;" +
            $"AccountName={AccountName};" +
            $"AccountKey={Convert.ToBase64String(new byte[64])};" +
            $"BlobEndpoint={this.endpoint}/{AccountName};";

        public static async Task<FakeBlobStorage> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            FakeBlobStorage storage = new(builder.Build());
            storage.app.Run(storage.HandleAsync);
            await storage.app.StartAsync();
            storage.endpoint = FakeWebsite.GetBoundAddress(storage.app);

            return storage;
        }

        /// <summary>
        /// Stores a blob directly, as if something else had uploaded it.
        /// </summary>
        public void Seed(string name, string contents, string contentType = "application/json")
        {
            lock (this.blobsLock)
            {
                this.blobs[name] = new StoredBlob(Encoding.UTF8.GetBytes(contents), contentType, null, NewETag());
            }
        }

        public async ValueTask DisposeAsync()
        {
            await this.app.StopAsync();
            await this.app.DisposeAsync();
        }

        private static string NewETag() => $"\"0x{Guid.NewGuid():N}\"";

        private async Task HandleAsync(HttpContext context)
        {
            string prefix = $"/{AccountName}/{ContainerName}/";
            // Kestrel's Request.Path is already partly decoded, so decode
            // the raw target exactly once, as Azure does
            string rawTarget = context.Features.Get<IHttpRequestFeature>()!.RawTarget;
            string path = Uri.UnescapeDataString(rawTarget.Split('?')[0]);

            context.Response.Headers["x-ms-request-id"] = Guid.NewGuid().ToString();
            context.Response.Headers["x-ms-version"] = "2020-08-04";

            if (!path.StartsWith(prefix) || context.Request.QueryString.HasValue)
            {
                this.RejectUnexpected(context, path);
                return;
            }

            string name = path.Substring(prefix.Length);

            if (HttpMethods.IsPut(context.Request.Method))
                await this.PutAsync(context, name);
            else if (HttpMethods.IsGet(context.Request.Method))
                await this.GetAsync(context, name);
            else
                this.RejectUnexpected(context, path);
        }

        private async Task PutAsync(HttpContext context, string name)
        {
            using MemoryStream body = new();
            await context.Request.Body.CopyToAsync(body);

            string? ifMatch = context.Request.Headers.IfMatch.FirstOrDefault();
            string? ifNoneMatch = context.Request.Headers.IfNoneMatch.FirstOrDefault();

            if (ifMatch != null || ifNoneMatch != null)
                this.BeforeConditionalPut?.Invoke(name);

            StoredBlob blob = new(
                body.ToArray(),
                context.Request.Headers["x-ms-blob-content-type"].FirstOrDefault(),
                context.Request.Headers["x-ms-blob-cache-control"].FirstOrDefault(),
                NewETag()
            );

            lock (this.blobsLock)
            {
                this.blobs.TryGetValue(name, out StoredBlob? existing);

                if (ifMatch != null && (existing == null || (ifMatch != "*" && ifMatch != existing.ETag)))
                {
                    WriteError(context, StatusCodes.Status412PreconditionFailed, "ConditionNotMet");
                    return;
                }

                if (ifNoneMatch == "*" && existing != null)
                {
                    WriteError(context, StatusCodes.Status409Conflict, "BlobAlreadyExists");
                    return;
                }

                this.blobs[name] = blob;
                this.uploads.Enqueue(name);
            }

            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.Headers.ETag = blob.ETag;
            context.Response.Headers.LastModified = DateTimeOffset.UtcNow.ToString("R");
            context.Response.Headers["x-ms-request-server-encrypted"] = "true";
        }

        private async Task GetAsync(HttpContext context, string name)
        {
            StoredBlob? blob;

            lock (this.blobsLock)
            {
                this.blobs.TryGetValue(name, out blob);
            }

            if (blob == null)
            {
                WriteError(context, StatusCodes.Status404NotFound, "BlobNotFound");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = blob.ContentType ?? "application/octet-stream";
            context.Response.ContentLength = blob.Contents.Length;
            context.Response.Headers.ETag = blob.ETag;
            context.Response.Headers.LastModified = DateTimeOffset.UtcNow.ToString("R");
            context.Response.Headers["x-ms-blob-type"] = "BlockBlob";
            context.Response.Headers["x-ms-creation-time"] = DateTimeOffset.UtcNow.ToString("R");
            context.Response.Headers["x-ms-lease-status"] = "unlocked";
            context.Response.Headers["x-ms-lease-state"] = "available";
            context.Response.Headers["x-ms-server-encrypted"] = "true";
            await context.Response.Body.WriteAsync(blob.Contents);
        }

        private void RejectUnexpected(HttpContext context, string path)
        {
            this.unexpectedRequests.Enqueue($"{context.Request.Method} {path}{context.Request.QueryString}");
            WriteError(context, StatusCodes.Status400BadRequest, "UnsupportedByFake");
        }

        private static void WriteError(HttpContext context, int statusCode, string errorCode)
        {
            context.Response.StatusCode = statusCode;
            context.Response.Headers["x-ms-error-code"] = errorCode;
        }
    }
}
