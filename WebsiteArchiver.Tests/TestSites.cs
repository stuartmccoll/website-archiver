using System.Text;
using WebsiteArchiver.Tests.Fakes;

namespace WebsiteArchiver.Tests
{
    /// <summary>
    /// Helpers for building fake sites and archiving them.
    /// </summary>
    public static class TestSites
    {
        public const string D = FakeWebsite.DomainPlaceholder;
        public const string Css = "body { color: #333; }";
        public const string External = "https://external.example/";

        /// <summary>
        /// Builds an HTML page that links the site stylesheet and contains
        /// the given anchors.
        /// </summary>
        public static string Page(string title, params string[] hrefs)
        {
            string anchors = string.Join(
                "\n    ",
                hrefs.Select(href => $"<a href=\"{href}\">{href}</a>")
            );

            return $"""
                <!DOCTYPE html>
                <html>
                  <head>
                    <title>{title}</title>
                    <link rel="stylesheet" href="/css/site.css">
                  </head>
                  <body>
                    {anchors}
                  </body>
                </html>
                """;
        }

        /// <summary>
        /// Builds an HTML page with the given markup in its body, and no
        /// stylesheet.
        /// </summary>
        public static string Body(string markup) =>
            $"<!DOCTYPE html><html><head><title>Test</title></head><body>{markup}</body></html>";

        /// <summary>
        /// Encodes text as windows-1252, for testing pages that aren't
        /// UTF-8.
        /// </summary>
        public static byte[] Windows1252(string text)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("windows-1252").GetBytes(text);
        }

        /// <summary>
        /// Starts a fake website serving /css/site.css plus whatever
        /// <paramref name="configure"/> adds, and archives it.
        /// </summary>
        /// <param name="configure">Adds pages to the site.</param>
        /// <param name="domain">Optional override for DOMAIN, given the
        /// site's root URL (with trailing slash).</param>
        /// <param name="storage">Optional storage to publish to.</param>
        public static async Task<(FakeWebsite Site, ArchiveRun Run)> ArchiveAsync(
            Action<FakeWebsite> configure,
            Func<string, string>? domain = null,
            FakeBlobStorage? storage = null
        )
        {
            FakeWebsite site = await FakeWebsite.StartAsync();
            site.WithStylesheet("/css/site.css", Css);
            configure(site);

            ArchiveRun run = await ArchiveRun.ExecuteAsync(site, domain?.Invoke(site.Domain), storage);
            return (site, run);
        }
    }
}
