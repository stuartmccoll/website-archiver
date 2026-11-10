using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WebsiteArchiver.Tests.Fakes
{
    /// <summary>
    /// A website served by Kestrel on a random local port. Page bodies may
    /// contain the {{DOMAIN}} placeholder, which is replaced with
    /// <see cref="Domain"/> when the page is served.
    /// </summary>
    public sealed class FakeWebsite : IAsyncDisposable
    {
        public const string DomainPlaceholder = "{{DOMAIN}}";

        private readonly WebApplication app;
        private readonly Dictionary<string, (string? Text, byte[]? Bytes, string? ContentType, int StatusCode)> pages = new();
        private readonly Dictionary<string, string> redirects = new();
        private readonly ConcurrentQueue<string> requestedPaths = new();

        private FakeWebsite(WebApplication app)
        {
            this.app = app;
        }

        /// <summary>
        /// The site's root URL, with a trailing slash, e.g.
        /// http://127.0.0.1:54321/
        /// </summary>
        public string Domain { get; private set; } = "";

        /// <summary>
        /// Every path requested from the site, including any query string,
        /// in order.
        /// </summary>
        public IReadOnlyList<string> RequestedPaths => this.requestedPaths.ToList();

        public static async Task<FakeWebsite> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            FakeWebsite site = new(builder.Build());
            site.app.Run(site.HandleAsync);
            await site.app.StartAsync();
            site.Domain = GetBoundAddress(site.app) + "/";

            return site;
        }

        /// <summary>
        /// Serves an HTML page at the given path, e.g. "/about/", with the
        /// given HTTP status code. Paths with no page return 404.
        /// </summary>
        public FakeWebsite WithPage(string path, string html, int statusCode = StatusCodes.Status200OK) =>
            this.With(path, html, "text/html; charset=utf-8", statusCode);

        /// <summary>
        /// Serves a CSS file at the given path, e.g. "/css/site.css".
        /// </summary>
        public FakeWebsite WithStylesheet(string path, string css) =>
            this.With(path, css, "text/css; charset=utf-8", StatusCodes.Status200OK);

        /// <summary>
        /// Serves a text file at the given path with the given Content-Type.
        /// Like pages, it may contain the domain placeholder.
        /// </summary>
        public FakeWebsite WithText(string path, string text, string contentType) =>
            this.With(path, text, contentType, StatusCodes.Status200OK);

        /// <summary>
        /// Serves a file's bytes, unchanged, at the given path. A null
        /// Content-Type sends no Content-Type header.
        /// </summary>
        public FakeWebsite WithFile(string path, byte[] contents, string? contentType)
        {
            this.pages[path] = (null, contents, contentType, StatusCodes.Status200OK);
            return this;
        }

        /// <summary>
        /// Permanently redirects (301) one path to another, e.g. "/about"
        /// to "/about/", as static hosts such as GitHub Pages do.
        /// </summary>
        public FakeWebsite WithRedirect(string path, string location)
        {
            this.redirects[path] = location;
            return this;
        }

        /// <summary>
        /// Returns a page's body as it is served, with the domain
        /// placeholder replaced.
        /// </summary>
        public string Rendered(string path) =>
            this.pages[path].Text!.Replace(DomainPlaceholder, this.Domain);

        public async ValueTask DisposeAsync()
        {
            await this.app.StopAsync();
            await this.app.DisposeAsync();
        }

        internal static string GetBoundAddress(WebApplication app)
        {
            IServerAddressesFeature addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!;

            return addresses.Addresses.Single().TrimEnd('/');
        }

        private FakeWebsite With(string path, string body, string contentType, int statusCode)
        {
            this.pages[path] = (body, null, contentType, statusCode);
            return this;
        }

        private async Task HandleAsync(HttpContext context)
        {
            string path = context.Request.Path.Value ?? "/";
            this.requestedPaths.Enqueue(path + context.Request.QueryString);

            if (this.redirects.TryGetValue(path, out string? location))
            {
                context.Response.Redirect(location, permanent: true);
                return;
            }

            if (!this.pages.TryGetValue(path, out var page))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.StatusCode = page.StatusCode;

            if (page.ContentType != null)
                context.Response.ContentType = page.ContentType;

            byte[] body = page.Bytes ?? Encoding.UTF8.GetBytes(this.Rendered(path));
            await context.Response.Body.WriteAsync(body);
        }
    }
}
