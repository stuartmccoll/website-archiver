using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace WebsiteArchiver.Tests.Fakes
{
    /// <summary>
    /// A minimal stand-in for the Azure Blob Storage REST API. It accepts
    /// Put Blob requests (which is all the archiver issues for small files)
    /// and records each blob's name and contents. Authentication is not
    /// checked.
    /// </summary>
    public sealed class FakeBlobStorage : IAsyncDisposable
    {
        public const string AccountName = "devstoreaccount1";
        public const string ContainerName = "archive";

        private readonly WebApplication app;
        private readonly ConcurrentQueue<(string Name, string Contents)> uploads = new();
        private readonly ConcurrentQueue<string> unexpectedRequests = new();
        private string endpoint = "";

        private FakeBlobStorage(WebApplication app)
        {
            this.app = app;
        }

        /// <summary>
        /// Every successful upload, in order, including repeat uploads of
        /// the same blob name.
        /// </summary>
        public IReadOnlyList<(string Name, string Contents)> Uploads => this.uploads.ToList();

        /// <summary>
        /// Any request other than a Put Blob into the expected container.
        /// </summary>
        public IReadOnlyList<string> UnexpectedRequests => this.unexpectedRequests.ToList();

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

        public async ValueTask DisposeAsync()
        {
            await this.app.StopAsync();
            await this.app.DisposeAsync();
        }

        private async Task HandleAsync(HttpContext context)
        {
            string prefix = $"/{AccountName}/{ContainerName}/";
            string path = Uri.UnescapeDataString(context.Request.Path.Value ?? "");

            if (context.Request.Method != HttpMethods.Put || !path.StartsWith(prefix))
            {
                this.unexpectedRequests.Enqueue($"{context.Request.Method} {path}");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using StreamReader reader = new(context.Request.Body);
            string contents = await reader.ReadToEndAsync();

            this.uploads.Enqueue((path.Substring(prefix.Length), contents));

            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.Headers["ETag"] = $"\"0x{Guid.NewGuid():N}\"";
            context.Response.Headers["Last-Modified"] = DateTimeOffset.UtcNow.ToString("R");
            context.Response.Headers["x-ms-request-id"] = Guid.NewGuid().ToString();
            context.Response.Headers["x-ms-version"] = "2020-08-04";
            context.Response.Headers["x-ms-request-server-encrypted"] = "true";
        }
    }
}
