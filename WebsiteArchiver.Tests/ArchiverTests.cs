using WebsiteArchiver.Tests.Fakes;
using static WebsiteArchiver.Tests.TestSites;

// The archiver is configured through process-wide environment variables, so
// tests must not run in parallel.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace WebsiteArchiver.Tests
{
    /// <summary>
    /// End-to-end tests that pin the archiver's crawling behaviour, so that
    /// dependency or framework upgrades can be checked for regressions.
    ///
    /// Some of the behaviour pinned here is a quirk rather than a deliberate
    /// design choice; those tests say so in their names.
    /// </summary>
    public class ArchiverTests
    {
        private static string SitePath(ArchiveRun run, string path) =>
            $"snapshots/{run.SnapshotId}/site/{path}";

        [Fact]
        public async Task Homepage_IsStoredAsIndexHtml()
        {
            var (site, run) = await ArchiveAsync(s =>
                s.WithPage("/", Page("Home", External))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/"), run.OriginalText("index.html"));
        }

        [Fact]
        public async Task Snapshot_IsNamedAfterTheUtcTimeItStarted()
        {
            var (site, run) = await ArchiveAsync(s =>
                s.WithPage("/", Page("Home", External))
            );
            await using var _ = site;

            // ArchiveRun also checks the time is within the run
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}Z$", run.SnapshotId);
        }

        [Fact]
        public async Task Stylesheet_IsStoredAtItsPath()
        {
            var (site, run) = await ArchiveAsync(s =>
                s.WithPage("/", Page("Home", External))
            );
            await using var _ = site;

            Assert.Equal(Css, run.Text("css/site.css"));
        }

        [Fact]
        public async Task TopLevelPage_IsStoredAsIndexHtmlInItsFolder()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}about/"))
                .WithPage("/about/", Page("About", External))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/about/"), run.OriginalText("about/index.html"));
        }

        [Theory]
        [InlineData("/blog/first-post/")]
        [InlineData("/blog/2024/deep-post/")]
        public async Task NestedPage_KeepsItsFolders(string path)
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}{path.TrimStart('/')}"))
                .WithPage(path, Page("Post", External))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered(path), run.OriginalText($"{path.TrimStart('/')}index.html"));
        }

        [Fact]
        public async Task PagesAreCrawledRecursively()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}one/"))
                .WithPage("/one/", Page("One", $"{D}two/"))
                .WithPage("/two/", Page("Two", $"{D}three/"))
                .WithPage("/three/", Page("Three", External))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/three/"), run.OriginalText("three/index.html"));
        }

        [Fact]
        public async Task OffSiteLinks_AreNotFetched()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", External, "https://github.com/example"))
            );
            await using var _ = site;

            Assert.Equal(new[] { "/", "/css/site.css" }, run.RequestedPaths.Order());
            Assert.Equal(new[] { "css/site.css", "index.html" }, run.Paths);
        }

        [Fact]
        public async Task PageLinkedMoreThanOnce_IsFetchedAndStoredOnce()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}about/", $"{D}blog/", $"{D}about/"))
                .WithPage("/blog/", Page("Blog", $"{D}about/"))
                .WithPage("/about/", Page("About", External))
            );
            await using var _ = site;

            Assert.Single(run.RequestedPaths, path => path == "/about/");
            Assert.Single(run.Uploads, name => name == SitePath(run, "about/index.html"));
        }

        [Fact]
        public async Task StylesheetUsedByEveryPage_IsFetchedOnce()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}about/"))
                .WithPage("/about/", Page("About", External))
            );
            await using var _ = site;

            Assert.Single(run.RequestedPaths, path => path == "/css/site.css");
        }

        [Fact]
        public async Task LinksBackToHomepage_DoNotStoreASecondCopy()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}about/", "/"))
                .WithPage("/about/", Page("About", D))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/"), run.OriginalText("index.html"));
            Assert.Equal(new[] { "about/index.html", "css/site.css", "index.html" }, run.Paths);
            Assert.Single(run.RequestedPaths, path => path == "/");
        }

        [Fact]
        public async Task PageUrlWithExtension_KeepsItsOwnExtension()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}page.html", $"{D}docs/guide.htm"))
                .WithPage("/page.html", Page("Page", External))
                .WithPage("/docs/guide.htm", Page("Guide", External))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/page.html"), run.OriginalText("page.html"));
            Assert.Equal(site.Rendered("/docs/guide.htm"), run.OriginalText("docs/guide.htm"));
        }

        [Fact]
        public async Task PageNameContainingAPeriod_IsStoredAsIndexHtml()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}posts/python-3.7-data-classes/"))
                .WithPage("/posts/python-3.7-data-classes/", Page("Post", External))
            );
            await using var _ = site;

            Assert.Equal(
                site.Rendered("/posts/python-3.7-data-classes/"),
                run.OriginalText("posts/python-3.7-data-classes/index.html")
            );
        }

        [Fact]
        public async Task RedirectedPage_ResolvesRelativeLinksAgainstItsFinalAddress()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}posts/second"))
                .WithRedirect("/posts/second", "/posts/second/")
                .WithPage("/posts/second/", Page("Second", "../first/"))
                .WithPage("/posts/first/", Page("First", External))
            );
            await using var _ = site;

            Assert.Empty(run.FailedAddresses);
            Assert.Equal(site.Rendered("/posts/second/"), run.OriginalText("posts/second/index.html"));
            Assert.Equal(site.Rendered("/posts/first/"), run.OriginalText("posts/first/index.html"));
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
                .WithPage("/about/", Page("About", External))
            );
            await using var _ = site;

            Assert.Single(run.Uploads, name => name == SitePath(run, "posts/second/index.html"));
            Assert.Single(run.RequestedPaths, path => path == "/about/");
        }

        [Fact]
        public async Task PageInADirectoryContainingAPeriod_IsStoredAsIndexHtml()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}v1.2/notes/"))
                .WithPage("/v1.2/notes/", Page("Notes", External))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/v1.2/notes/"), run.OriginalText("v1.2/notes/index.html"));
        }

        [Fact]
        public async Task TypicalSite_ProducesExpectedSetOfFiles()
        {
            string[] nav = { D, $"{D}about/", $"{D}blog/", "https://github.com/example" };

            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", nav.Append($"{D}blog/first-post/").Append($"{D}index.xml").ToArray()))
                .WithPage("/about/", Page("About", nav))
                .WithPage("/blog/", Page("Blog", nav.Append($"{D}blog/first-post/").Append($"{D}blog/2024/second-post/").ToArray()))
                .WithPage("/blog/first-post/", Page("First post", nav))
                .WithPage("/blog/2024/second-post/", Page("Second post", nav))
                .WithText("/index.xml", "<rss version=\"2.0\"></rss>", "application/rss+xml")
            );
            await using var _ = site;

            Assert.Equal(
                new[]
                {
                    "about/index.html",
                    "blog/2024/second-post/index.html",
                    "blog/first-post/index.html",
                    "blog/index.html",
                    "css/site.css",
                    "index.html",
                    "index.xml",
                },
                run.Paths
            );
            Assert.Empty(run.FailedAddresses);
        }

        [Fact]
        public async Task HomepageWithoutStylesheet_IsArchived()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", $"<html><body><a href=\"{External}\">External</a></body></html>")
            );
            await using var _ = site;

            Assert.Equal(new[] { "index.html" }, run.Paths);
            Assert.Equal(site.Rendered("/"), run.OriginalText("index.html"));
        }

        [Fact]
        public async Task PageWithoutLinks_IsArchived()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}about/"))
                .WithPage("/about/", "<html><body><p>No links here.</p></body></html>")
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/about/"), run.OriginalText("about/index.html"));
        }

        [Fact]
        public async Task AbsoluteStylesheetUrl_IsArchived()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", $"<html><head><link rel=\"stylesheet\" href=\"{D}css/site.css\"></head><body></body></html>")
            );
            await using var _ = site;

            Assert.Equal(Css, run.Text("css/site.css"));
        }

        [Fact]
        public async Task RootRelativeLinks_AreFollowed()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", "/contact/"))
                .WithPage("/contact/", Page("Contact", External))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/contact/"), run.OriginalText("contact/index.html"));
        }

        [Fact]
        public async Task DomainWithoutTrailingSlash_IsArchived()
        {
            var (site, run) = await ArchiveAsync(
                s => s
                    .WithPage("/", Page("Home", $"{D}about/"))
                    .WithPage("/about/", Page("About", External)),
                domain: root => root.TrimEnd('/')
            );
            await using var _ = site;

            Assert.Equal(new[] { "about/index.html", "css/site.css", "index.html" }, run.Paths);
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
                      <body><a href="{External}">External</a></body>
                    </html>
                    """)
            );
            await using var _ = site;

            Assert.Equal(new[] { "css/site.css", "index.html" }, run.Paths);
            Assert.Empty(run.FailedAddresses);
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
            Assert.Equal(new[] { "css/site.css", "index.html" }, run.Paths);
        }

        [Fact]
        public async Task LinksDifferingOnlyByFragment_AreFetchedOnce()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}about/", $"{D}about/#team"))
                .WithPage("/about/", Page("About", External))
            );
            await using var _ = site;

            Assert.Single(run.RequestedPaths, path => path == "/about/");
            Assert.Equal(site.Rendered("/about/"), run.OriginalText("about/index.html"));
        }

        [Fact]
        public async Task RelativeLinks_ResolveAgainstThePageTheyAreOn()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", "blog/"))
                .WithPage("/blog/", Page("Blog", "first-post/", "../about/"))
                .WithPage("/blog/first-post/", Page("First post", External))
                .WithPage("/about/", Page("About", External))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/blog/first-post/"), run.OriginalText("blog/first-post/index.html"));
            Assert.Equal(site.Rendered("/about/"), run.OriginalText("about/index.html"));
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
                .WithPage("/about/", Page("About", External))
            );
            await using var _ = site;

            Assert.Equal(new[] { "/about/" }, run.RequestedPaths.Where(path => path.StartsWith("/about/")));
            Assert.Equal(new[] { "about/index.html", "css/site.css", "index.html" }, run.Paths);
        }

        // Error handling: a file that can't be fetched is reported and
        // skipped, without stopping the rest of the run.

        [Fact]
        public async Task MissingPage_IsReportedAndNotArchived()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}missing/", $"{D}about/"))
                .WithPage("/about/", Page("About", External))
            );
            await using var _ = site;

            Assert.Equal(new[] { "about/index.html", "css/site.css", "index.html" }, run.Paths);
            Assert.Equal(new[] { $"{site.Domain}missing/" }, run.FailedAddresses);
        }

        [Fact]
        public async Task LinksOnAnErrorPage_AreNotFollowed()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}broken/"))
                .WithPage("/broken/", Page("Broken", $"{D}about/"), statusCode: 500)
                .WithPage("/about/", Page("About", External))
            );
            await using var _ = site;

            Assert.DoesNotContain("/about/", run.RequestedPaths);
            Assert.Equal(new[] { "css/site.css", "index.html" }, run.Paths);
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
                .WithPage("/about/", Page("About", External))
            );
            await using var _ = site;

            Assert.Equal(new[] { "about/index.html", "css/site.css", "index.html" }, run.Paths);
            Assert.Equal(new[] { $"{site.Domain}css/missing.css" }, run.FailedAddresses);
        }

        [Fact]
        public async Task SuccessfulRun_ReportsNoFailures()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", External))
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
        public async Task UnavailableHomepage_FailsTheRunWithoutPublishingAnything()
        {
            await using FakeWebsite site = await FakeWebsite.StartAsync();
            await using FakeBlobStorage storage = await FakeBlobStorage.StartAsync();
            site.WithPage("/", Page("Home", External), statusCode: 503);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ArchiveRun.ExecuteAsync(site, storage: storage)
            );
            Assert.Empty(storage.Uploads);
        }
    }
}
