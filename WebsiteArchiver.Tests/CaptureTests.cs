using static WebsiteArchiver.Tests.TestSites;

namespace WebsiteArchiver.Tests
{
    /// <summary>
    /// Tests that every file a site uses is captured, stored under a path
    /// that mirrors its URL, and kept exactly as it was served.
    /// </summary>
    public class CaptureTests
    {
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xFE, 0x80 };

        [Theory]
        [InlineData("<img src=\"/img/photo.png\">", new[] { "img/photo.png" })]
        [InlineData("<img src=\"img/photo.png\">", new[] { "img/photo.png" })]
        [InlineData("<img srcset=\"/img/small.png 1x, /img/large.png 2x\">", new[] { "img/small.png", "img/large.png" })]
        [InlineData("<img srcset=\"data:image/png;base64,iVBO,Rw0 1x, /img/large.png 2x\">", new[] { "img/large.png" })]
        [InlineData("<picture><source srcset=\"/img/photo.webp\" type=\"image/webp\"><img src=\"/img/photo.png\"></picture>", new[] { "img/photo.webp", "img/photo.png" })]
        [InlineData("<script src=\"/js/site.js\"></script>", new[] { "js/site.js" })]
        [InlineData("<link rel=\"icon\" href=\"/favicon.ico\">", new[] { "favicon.ico" })]
        [InlineData("<link rel=\"shortcut icon\" href=\"/favicon.ico\">", new[] { "favicon.ico" })]
        [InlineData("<link rel=\"apple-touch-icon\" sizes=\"180x180\" href=\"/apple-touch-icon.png\">", new[] { "apple-touch-icon.png" })]
        [InlineData("<link rel=\"mask-icon\" href=\"/safari-pinned-tab.svg\" color=\"#000\">", new[] { "safari-pinned-tab.svg" })]
        [InlineData("<link rel=\"preload\" href=\"/fonts/body.woff2\" as=\"font\" crossorigin>", new[] { "fonts/body.woff2" })]
        [InlineData("<video src=\"/media/clip.mp4\" poster=\"/media/poster.jpg\"></video>", new[] { "media/clip.mp4", "media/poster.jpg" })]
        [InlineData("<video><source src=\"/media/clip.webm\"><track src=\"/media/captions.vtt\"></video>", new[] { "media/clip.webm", "media/captions.vtt" })]
        [InlineData("<audio src=\"/media/sound.mp3\"></audio>", new[] { "media/sound.mp3" })]
        [InlineData("<object data=\"/files/diagram.svg\"></object><embed src=\"/files/animation.swf\">", new[] { "files/diagram.svg", "files/animation.swf" })]
        [InlineData("<iframe src=\"/embed/frame.bin\"></iframe>", new[] { "embed/frame.bin" })]
        [InlineData("<div style=\"background: url('/img/bg.png')\"></div>", new[] { "img/bg.png" })]
        [InlineData("<style>body { background: url(/img/bg.png); }</style>", new[] { "img/bg.png" })]
        [InlineData("<style>@import \"/css/extra.css\";</style>", new[] { "css/extra.css" })]
        [InlineData("<base href=\"/assets/\"><img src=\"photo.png\">", new[] { "assets/photo.png" })]
        public async Task FileAPageUses_IsCaptured(string markup, string[] expectedPaths)
        {
            var (site, run) = await ArchiveAsync(s =>
            {
                s.WithPage("/", Body(markup));

                foreach (string path in expectedPaths)
                    s.WithFile("/" + path, Png, "application/octet-stream");
            });
            await using var _ = site;

            Assert.Empty(run.FailedAddresses);
            Assert.Equal(
                expectedPaths.Append("index.html").Order(StringComparer.Ordinal),
                run.Paths
            );
        }

        [Fact]
        public async Task FilesOnEveryPage_AreCaptured()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}blog/post/"))
                .WithPage("/blog/post/", Body("<link rel=\"stylesheet\" href=\"post.css\"><img src=\"diagram.png\">"))
                .WithStylesheet("/blog/post/post.css", "p { margin: 0; }")
                .WithFile("/blog/post/diagram.png", Png, "image/png")
            );
            await using var _ = site;

            Assert.Contains("blog/post/post.css", run.Paths);
            Assert.Contains("blog/post/diagram.png", run.Paths);
        }

        [Fact]
        public async Task FilesAStylesheetUses_AreCaptured()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Body("<link rel=\"stylesheet\" href=\"/css/main.css\">"))
                .WithStylesheet("/css/main.css", """
                    @import "base.css";
                    @import url(print.css) print;
                    /* url(/ignored.png) is in a comment */
                    @font-face { src: url("fonts/body.woff2") format("woff2"), url('/fonts/body.woff') format("woff"); }
                    .hero { background: url(../img/hero.jpg); }
                    .dot { background: url(data:image/png;base64,iVBORw0KGgo=); }
                    .glow { filter: url(#glow); }
                    """)
                .WithStylesheet("/css/base.css", "body { background: url(/img/paper.png); }")
                .WithStylesheet("/css/print.css", "body { color: black; }")
                .WithFile("/css/fonts/body.woff2", Png, "font/woff2")
                .WithFile("/fonts/body.woff", Png, "font/woff")
                .WithFile("/img/hero.jpg", Png, "image/jpeg")
                .WithFile("/img/paper.png", Png, "image/png")
            );
            await using var _ = site;

            Assert.Empty(run.FailedAddresses);
            Assert.Equal(
                new[]
                {
                    "css/base.css",
                    "css/fonts/body.woff2",
                    "css/main.css",
                    "css/print.css",
                    "fonts/body.woff",
                    "img/hero.jpg",
                    "img/paper.png",
                    "index.html",
                },
                run.Paths
            );
        }

        [Theory]
        [InlineData("application/manifest+json")]
        [InlineData("application/json")]
        public async Task WebManifestAndItsIcons_AreCaptured(string contentType)
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Body("<link rel=\"manifest\" href=\"/site.webmanifest\">"))
                .WithText("/site.webmanifest", """
                    {
                      "name": "Example",
                      "icons": [
                        { "src": "/android-chrome-192x192.png", "sizes": "192x192" },
                        { "src": "icons/512.png", "sizes": "512x512" }
                      ]
                    }
                    """, contentType)
                .WithFile("/android-chrome-192x192.png", Png, "image/png")
                .WithFile("/icons/512.png", Png, "image/png")
            );
            await using var _ = site;

            Assert.Empty(run.FailedAddresses);
            Assert.Equal(
                new[] { "android-chrome-192x192.png", "icons/512.png", "index.html", "site.webmanifest" },
                run.Paths
            );
        }

        [Fact]
        public async Task InvalidWebManifest_IsStoredWithoutStoppingTheRun()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Body("<link rel=\"manifest\" href=\"/site.webmanifest\">"))
                .WithText("/site.webmanifest", "{ not json", "application/manifest+json")
            );
            await using var _ = site;

            Assert.Equal("{ not json", run.Text("site.webmanifest"));
        }

        [Fact]
        public async Task BinaryFile_IsStoredByteForByte()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Body("<img src=\"/img/photo.png\">"))
                .WithFile("/img/photo.png", Png, "image/png")
            );
            await using var _ = site;

            Assert.Equal(Png, run.Files["img/photo.png"].Contents);
        }

        [Fact]
        public async Task PdfLinkedFromAPage_IsStoredAsAPdf()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", "/files/cv.pdf"))
                .WithFile("/files/cv.pdf", Png, "application/pdf")
            );
            await using var _ = site;

            Assert.Equal(Png, run.Files["files/cv.pdf"].Contents);
            Assert.Equal("application/pdf", run.Files["files/cv.pdf"].ContentType);
        }

        [Fact]
        public async Task LinkedFeed_IsStoredButNotCrawled()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", $"{D}index.xml"))
                .WithText("/index.xml", $"<rss><a href=\"{D}hidden/\">Hidden</a></rss>", "application/rss+xml")
                .WithPage("/hidden/", Page("Hidden", External))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/index.xml"), run.Text("index.xml"));
            Assert.DoesNotContain("/hidden/", run.RequestedPaths);
        }

        [Fact]
        public async Task EachFile_IsStoredWithTheContentTypeItWasServedWith()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", "/files/cv.pdf", "/files/notes"))
                .WithFile("/files/cv.pdf", Png, "application/pdf")
                .WithFile("/files/notes", Png, contentType: null)
            );
            await using var _ = site;

            Assert.Equal("text/html; charset=utf-8", run.Files["index.html"].ContentType);
            Assert.Equal("text/css; charset=utf-8", run.Files["css/site.css"].ContentType);
            Assert.Equal("application/pdf", run.Files["files/cv.pdf"].ContentType);
            Assert.Equal("application/octet-stream", run.Files["files/notes"].ContentType);
        }

        [Theory]
        [InlineData("about/", "/about/", "text/html", "about/index.html")]
        [InlineData("about", "/about", "text/html", "about/index.html")]
        [InlineData("page.html", "/page.html", "text/html", "page.html")]
        [InlineData("docs/GUIDE.HTM", "/docs/GUIDE.HTM", "text/html", "docs/GUIDE.HTM")]
        [InlineData("posts/python-3.7-data-classes/", "/posts/python-3.7-data-classes/", "text/html", "posts/python-3.7-data-classes/index.html")]
        [InlineData("v1.2/notes", "/v1.2/notes", "application/xhtml+xml", "v1.2/notes/index.html")]
        [InlineData("files/cv.pdf", "/files/cv.pdf", "application/pdf", "files/cv.pdf")]
        [InlineData("CNAME", "/CNAME", "text/plain", "CNAME")]
        [InlineData("blog/caf%C3%A9/", "/blog/café/", "text/html", "blog/café/index.html")]
        [InlineData("blog/café/", "/blog/café/", "text/html", "blog/café/index.html")]
        [InlineData("images/my%20photo.png", "/images/my photo.png", "image/png", "images/my photo.png")]
        [InlineData("odd%2Fname.txt", "/odd%2Fname.txt", "text/plain", "odd%2Fname.txt")]
        public async Task Address_IsMappedToAPathThatMirrorsIt(
            string href,
            string servedPath,
            string contentType,
            string expectedPath
        )
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", href))
                .WithText(servedPath, "<p>Content</p>", contentType)
            );
            await using var _ = site;

            Assert.Empty(run.FailedAddresses);
            Assert.Equal(new[] { "css/site.css", expectedPath, "index.html" }.Order(StringComparer.Ordinal), run.Paths);
        }

        [Fact]
        public async Task FilesOutsideTheDomainsPath_AreCaptured_ButPagesOutsideItAreNot()
        {
            var (site, run) = await ArchiveAsync(
                s => s
                    .WithPage("/blog/", Page("Blog", "/about/", "/blog/post/"))
                    .WithPage("/blog/post/", Page("Post", External))
                    .WithPage("/about/", Page("About", External)),
                domain: root => root + "blog/"
            );
            await using var _ = site;

            Assert.Equal(new[] { "blog/index.html", "blog/post/index.html", "css/site.css" }, run.Paths);
            Assert.DoesNotContain("/about/", run.RequestedPaths);
            Assert.Empty(run.FailedAddresses);
        }

        [Fact]
        public async Task HomepageRedirect_SetsTheSiteRoot()
        {
            // As when DOMAIN is http:// and the site redirects to https://,
            // the site's own links point beneath where DOMAIN redirects to
            var (site, run) = await ArchiveAsync(
                s => s
                    .WithRedirect("/old/", "/new/")
                    .WithPage("/new/", Page("Home", $"{D}new/about/"))
                    .WithPage("/new/about/", Page("About", External)),
                domain: root => root + "old/"
            );
            await using var _ = site;

            Assert.Equal(new[] { "css/site.css", "new/about/index.html", "new/index.html" }, run.Paths);
            Assert.Equal($"{site.Domain}new/", run.Manifest.GetProperty("root").GetString());
            Assert.Equal("new/index.html", run.Manifest.GetProperty("homepage").GetString());
        }

        [Fact]
        public async Task FilesOnOtherHosts_AreNotFetched()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Body("""
                    <link rel="icon" href="https://cdn.invalid/favicon.ico">
                    <img src="https://cdn.invalid/photo.png">
                    <script src="//cdn.invalid/site.js"></script>
                    """))
            );
            await using var _ = site;

            Assert.Equal(new[] { "/" }, run.RequestedPaths);
            Assert.Empty(run.FailedAddresses);
        }

        [Fact]
        public async Task MissingImage_IsReportedWithoutStoppingTheRun()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Body("<img src=\"/img/missing.png\"><img src=\"/img/photo.png\">"))
                .WithFile("/img/photo.png", Png, "image/png")
            );
            await using var _ = site;

            Assert.Contains("img/photo.png", run.Paths);
            Assert.Equal(new[] { $"{site.Domain}img/missing.png" }, run.FailedAddresses);
        }

        [Fact]
        public async Task Quirk_SecondAddressForTheSamePath_IsSkipped()
        {
            // /about and /about/ both map to about/index.html. GitHub Pages
            // redirects one to the other, but a server that serves both
            // directly gets only the first one stored.
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", "/about", "/about/"))
                .WithPage("/about", Page("About without slash", External))
                .WithPage("/about/", Page("About with slash", External))
            );
            await using var _ = site;

            Assert.Equal(site.Rendered("/about"), run.OriginalText("about/index.html"));
            Assert.Empty(run.FailedAddresses);
        }
    }
}
