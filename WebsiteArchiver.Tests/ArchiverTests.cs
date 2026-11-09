using WebsiteArchiver.Tests.Fakes;

// The archiver is configured through process-wide environment variables, so
// tests must not run in parallel.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace WebsiteArchiver.Tests
{
    /// <summary>
    /// End-to-end tests that pin the archiver's current behaviour, so that
    /// dependency or framework upgrades can be checked for regressions.
    ///
    /// Some of the behaviour pinned here is a quirk rather than a deliberate
    /// design choice; those tests say so in their names.
    /// </summary>
    public class ArchiverTests
    {
        private const string D = FakeWebsite.DomainPlaceholder;
        private const string Css = "body { color: #333; }";

        /// <summary>
        /// Builds an HTML page that links the site stylesheet and contains
        /// the given anchors.
        /// </summary>
        private static string Page(string title, params string[] hrefs)
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
                    <link rel="stylesheet" href="css/site.css">
                  </head>
                  <body>
                    {anchors}
                  </body>
                </html>
                """;
        }

        /// <summary>
        /// Starts a fake website serving /css/site.css plus whatever
        /// <paramref name="configure"/> adds, and archives it.
        /// </summary>
        /// <param name="configure">Adds pages to the site.</param>
        /// <param name="domain">Optional override for DOMAIN, given the
        /// site's root URL (with trailing slash).</param>
        private static async Task<(FakeWebsite Site, ArchiveRun Run)> ArchiveAsync(
            Action<FakeWebsite> configure,
            Func<string, string>? domain = null
        )
        {
            FakeWebsite site = await FakeWebsite.StartAsync();
            site.WithStylesheet("/css/site.css", Css);
            configure(site);

            ArchiveRun run = await ArchiveRun.ExecuteAsync(site, domain?.Invoke(site.Domain));
            return (site, run);
        }

        [Fact]
        public async Task Homepage_IsStoredAsMainHtml()
        {
            var (site, run) = await ArchiveAsync(s =>
                s.WithPage("/", Page("Home", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/"), run.Blobs["main.html"]);
        }

        [Fact]
        public async Task BlobNames_ArePrefixedWithTodaysDate()
        {
            var (site, run) = await ArchiveAsync(s =>
                s.WithPage("/", Page("Home", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Matches("^[0-3][0-9][01][0-9][0-9]{4}$", run.DatePrefix);
            Assert.NotEmpty(run.Uploads);
        }

        [Fact]
        public async Task Stylesheet_IsStoredUnderItsFileNameWithoutItsDirectory()
        {
            var (site, run) = await ArchiveAsync(s =>
                s.WithPage("/", Page("Home", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(Css, run.Blobs["site.css"]);
            Assert.DoesNotContain("css/site.css", run.Blobs.Keys);
        }

        [Fact]
        public async Task TopLevelPage_IsStoredAsPageNameDotHtml()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}about/"))
                .WithPage("/about/", Page("About", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/about/"), run.Blobs["about.html"]);
        }

        [Fact]
        public async Task NestedPage_IsStoredInAFolderNamedAfterItsFirstPathSegment()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}blog/first-post/"))
                .WithPage("/blog/first-post/", Page("First post", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/blog/first-post/"), run.Blobs["blog/first-post.html"]);
        }

        [Fact]
        public async Task DeeplyNestedPage_JoinsRemainingPathSegmentsWithHyphens()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}blog/2024/deep-post/"))
                .WithPage("/blog/2024/deep-post/", Page("Deep post", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(
                site.Rendered("/blog/2024/deep-post/"),
                run.Blobs["blog/2024-deep-post.html"]
            );
        }

        [Fact]
        public async Task PagesAreCrawledRecursively()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}one/"))
                .WithPage("/one/", Page("One", $"{D}two/"))
                .WithPage("/two/", Page("Two", $"{D}three/"))
                .WithPage("/three/", Page("Three", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/three/"), run.Blobs["three.html"]);
        }

        [Fact]
        public async Task OffSiteAndXmlLinks_AreNotFetched()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page(
                    "Home",
                    "https://external.example/",
                    $"{D}index.xml",
                    $"{D}blog/index.xml"
                ))
            );
            await using var _ = site;

            Assert.Equal(new[] { "/", "/css/site.css" }, run.RequestedPaths.Order());
            Assert.Equal(new[] { "main.html", "site.css" }, run.Blobs.Keys.Order());
        }

        [Fact]
        public async Task PageLinkedMoreThanOnce_IsFetchedAndStoredOnce()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}about/", $"{D}blog/", $"{D}about/"))
                .WithPage("/blog/", Page("Blog", $"{D}about/"))
                .WithPage("/about/", Page("About", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Single(run.RequestedPaths, path => path == "/about/");
            Assert.Single(run.Uploads, upload => upload.Name == "about.html");
        }

        [Fact]
        public async Task LinksBackToHomepage_DoNotStoreASecondCopy()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}about/", "/"))
                .WithPage("/about/", Page("About", D))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/"), run.Blobs["main.html"]);
            Assert.Equal(new[] { "about.html", "main.html", "site.css" }, run.Blobs.Keys.Order(StringComparer.Ordinal));
            Assert.Single(run.RequestedPaths, path => path == "/");
        }

        [Fact]
        public async Task PageUrlWithExtension_KeepsItsOwnExtension()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}page.html", $"{D}docs/guide.htm"))
                .WithPage("/page.html", Page("Page", "https://external.example/"))
                .WithPage("/docs/guide.htm", Page("Guide", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/page.html"), run.Blobs["page.html"]);
            Assert.Equal(site.Rendered("/docs/guide.htm"), run.Blobs["docs/guide.htm"]);
        }

        [Fact]
        public async Task PageNameContainingAPeriod_IsStoredAsHtml()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}posts/python-3.7-data-classes/"))
                .WithPage("/posts/python-3.7-data-classes/", Page("Post", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(
                site.Rendered("/posts/python-3.7-data-classes/"),
                run.Blobs["posts/python-3.7-data-classes.html"]
            );
        }

        [Fact]
        public async Task RedirectedPage_ResolvesRelativeLinksAgainstItsFinalAddress()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}posts/second"))
                .WithRedirect("/posts/second", "/posts/second/")
                .WithPage("/posts/second/", Page("Second", "../first/"))
                .WithPage("/posts/first/", Page("First", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Empty(run.FailedAddresses);
            Assert.Equal(site.Rendered("/posts/second/"), run.Blobs["posts/second.html"]);
            Assert.Equal(site.Rendered("/posts/first/"), run.Blobs["posts/first.html"]);
        }

        [Theory]
        [InlineData("posts/second", "posts/second/")]
        [InlineData("posts/second/", "posts/second")]
        public async Task PageLinkedWithAndWithoutTrailingSlash_IsStoredAndCrawledOnce(
            string firstLink,
            string secondLink
        )
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}{firstLink}", $"{D}{secondLink}"))
                .WithRedirect("/posts/second", "/posts/second/")
                .WithPage("/posts/second/", Page("Second", $"{D}about/"))
                .WithPage("/about/", Page("About", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Single(run.Uploads, upload => upload.Name == "posts/second.html");
            Assert.Single(run.RequestedPaths, path => path == "/about/");
        }

        [Fact]
        public async Task PageInADirectoryContainingAPeriod_IsStoredAsHtml()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}v1.2/notes/"))
                .WithPage("/v1.2/notes/", Page("Notes", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/v1.2/notes/"), run.Blobs["v1.2/notes.html"]);
        }

        [Fact]
        public async Task TypicalSite_ProducesExpectedSetOfBlobs()
        {
            string[] nav = { D, $"{D}about/", $"{D}blog/", "https://github.com/example" };

            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", nav.Append($"{D}blog/first-post/").Append($"{D}index.xml").ToArray()))
                .WithPage("/about/", Page("About", nav))
                .WithPage("/blog/", Page("Blog", nav.Append($"{D}blog/first-post/").Append($"{D}blog/2024/second-post/").ToArray()))
                .WithPage("/blog/first-post/", Page("First post", nav))
                .WithPage("/blog/2024/second-post/", Page("Second post", nav))
            );
            await using var _ = site;

            Assert.Equal(
                new[]
                {
                    "about.html",
                    "blog.html",
                    "blog/2024-second-post.html",
                    "blog/first-post.html",
                    "main.html",
                    "site.css",
                },
                run.Blobs.Keys.Order(StringComparer.Ordinal)
            );
        }

        [Fact]
        public async Task HomepageWithoutStylesheet_IsArchived()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", "<html><body><a href=\"https://external.example/\">External</a></body></html>")
            );
            await using var _ = site;

            Assert.Equal(new[] { "main.html" }, run.Blobs.Keys);
            Assert.Equal(site.Rendered("/"), run.Blobs["main.html"]);
        }

        [Fact]
        public async Task PageWithoutLinks_IsArchived()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}about/"))
                .WithPage("/about/", "<html><body><p>No links here.</p></body></html>")
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/about/"), run.Blobs["about.html"]);
        }

        [Fact]
        public async Task AbsoluteStylesheetUrl_IsArchived()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", $"<html><head><link rel=\"stylesheet\" href=\"{D}css/site.css\"></head><body></body></html>")
            );
            await using var _ = site;

            Assert.Equal(Css, run.Blobs["site.css"]);
        }

        [Fact]
        public async Task RootRelativeLinks_AreFollowed()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", "/contact/"))
                .WithPage("/contact/", Page("Contact", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/contact/"), run.Blobs["contact.html"]);
        }

        [Fact]
        public async Task DomainWithoutTrailingSlash_IsArchived()
        {
            var (site, run) = await ArchiveAsync(
                s => s
                    .WithPage("/", Page("Home", $"{D}about/"))
                    .WithPage("/about/", Page("About", "https://external.example/")),
                domain: root => root.TrimEnd('/')
            );
            await using var _ = site;

            Assert.Equal(
                new[] { "about.html", "main.html", "site.css" },
                run.Blobs.Keys.Order(StringComparer.Ordinal)
            );
        }

        [Fact]
        public async Task OffSiteStylesheet_IsSkipped()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", $"""
                    <html>
                      <head>
                        <link rel="stylesheet" href="https://cdn.invalid/framework.css">
                        <link rel="stylesheet" href="css/site.css">
                      </head>
                      <body><a href="https://external.example/">External</a></body>
                    </html>
                    """)
            );
            await using var _ = site;

            Assert.Equal(new[] { "main.html", "site.css" }, run.Blobs.Keys.Order());
        }

        [Fact]
        public async Task FragmentMailtoAndScriptLinks_AreNotFetched()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page(
                    "Home",
                    "#top",
                    "mailto:someone@example.com",
                    "tel:+441234567890",
                    "javascript:void(0)"
                ))
            );
            await using var _ = site;

            Assert.Equal(new[] { "/", "/css/site.css" }, run.RequestedPaths.Order());
            Assert.Equal(new[] { "main.html", "site.css" }, run.Blobs.Keys.Order());
        }

        [Fact]
        public async Task LinksDifferingOnlyByFragment_AreFetchedOnce()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}about/", $"{D}about/#team"))
                .WithPage("/about/", Page("About", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Single(run.RequestedPaths, path => path == "/about/");
            Assert.Equal(site.Rendered("/about/"), run.Blobs["about.html"]);
        }

        [Fact]
        public async Task RelativeLinks_ResolveAgainstThePageTheyAreOn()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", "blog/"))
                .WithPage("/blog/", Page("Blog", "first-post/", "../about/"))
                .WithPage("/blog/first-post/", Page("First post", "https://external.example/"))
                .WithPage("/about/", Page("About", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/blog/first-post/"), run.Blobs["blog/first-post.html"]);
            Assert.Equal(site.Rendered("/about/"), run.Blobs["about.html"]);
        }

        [Fact]
        public async Task QueryStrings_AreDroppedSoEachPageIsFetchedOnce()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page(
                    "Home",
                    $"{D}about/?utm_source=nav",
                    $"{D}about/?utm_source=footer&amp;utm_medium=web",
                    $"{D}about/"
                ))
                .WithPage("/about/", Page("About", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(new[] { "/about/" }, run.RequestedPaths.Where(path => path.StartsWith("/about/")));
            Assert.Equal(new[] { "about.html", "main.html", "site.css" }, run.Blobs.Keys.Order(StringComparer.Ordinal));
        }

        // Error handling: a page or stylesheet that can't be fetched is
        // reported and skipped, without stopping the rest of the run.

        [Fact]
        public async Task MissingPage_IsReportedAndNotArchived()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}missing/", $"{D}about/"))
                .WithPage("/about/", Page("About", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(new[] { "about.html", "main.html", "site.css" }, run.Blobs.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(new[] { $"{site.Domain}missing/" }, run.FailedAddresses);
        }

        [Fact]
        public async Task LinksOnAnErrorPage_AreNotFollowed()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}broken/"))
                .WithPage("/broken/", Page("Broken", $"{D}about/"), statusCode: 500)
                .WithPage("/about/", Page("About", "https://external.example/"))
            );
            await using var _ = site;

            Assert.DoesNotContain("/about/", run.RequestedPaths);
            Assert.Equal(new[] { "main.html", "site.css" }, run.Blobs.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(new[] { $"{site.Domain}broken/" }, run.FailedAddresses);
        }

        [Fact]
        public async Task MissingStylesheet_IsReportedWithoutStoppingTheRun()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", $"""
                    <html>
                      <head>
                        <link rel="stylesheet" href="css/missing.css">
                        <link rel="stylesheet" href="css/site.css">
                      </head>
                      <body><a href="{D}about/">About</a></body>
                    </html>
                    """)
                .WithPage("/about/", Page("About", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Equal(new[] { "about.html", "main.html", "site.css" }, run.Blobs.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(new[] { $"{site.Domain}css/missing.css" }, run.FailedAddresses);
        }

        [Fact]
        public async Task SuccessfulRun_ReportsNoFailures()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", "https://external.example/"))
            );
            await using var _ = site;

            Assert.Empty(run.FailedAddresses);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("example.com")]
        [InlineData("ftp://example.com/")]
        public void MissingOrInvalidDomain_IsReportedClearly(string? domain)
        {
            string? original = Environment.GetEnvironmentVariable("DOMAIN");

            try
            {
                Environment.SetEnvironmentVariable("DOMAIN", domain);

                InvalidOperationException exception =
                    Assert.Throws<InvalidOperationException>(() => new Archiver());

                Assert.Contains("DOMAIN", exception.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOMAIN", original);
            }
        }

        [Fact]
        public async Task UnavailableHomepage_FailsTheRun()
        {
            await using FakeWebsite site = await FakeWebsite.StartAsync();
            site.WithPage("/", Page("Home", "https://external.example/"), statusCode: 503);

            await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveRun.ExecuteAsync(site));
        }
    }
}
