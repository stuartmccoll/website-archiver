using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using WebsiteArchiver.Tests.Fakes;
using static WebsiteArchiver.Tests.TestSites;

namespace WebsiteArchiver.Tests
{
    /// <summary>
    /// Tests that the links in a snapshot's site/ copy are rewritten so it
    /// can be browsed on its own, from any address.
    /// </summary>
    public class RewriteTests
    {
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF };

        // Where the acceptance test pretends the snapshot is served from:
        // a host and folder that have nothing to do with the original site
        private const string SnapshotHost = "snapshot.invalid";
        private const string SnapshotSite = $"https://{SnapshotHost}/any/folder/snapshots/x/site/";

        /// <summary>
        /// A site that uses every kind of reference that's rewritten, from
        /// pages and stylesheets at different depths.
        /// </summary>
        private static void ConfigureFullSite(FakeWebsite site) => site
            .WithPage("/", $$"""
                <!DOCTYPE html>
                <html>
                  <head>
                    <meta charset="utf-8">
                    <link rel="stylesheet" href="/css/site.css?v=3">
                    <link rel="icon" href="favicon.ico">
                    <link rel="manifest" href="{{D}}site.webmanifest">
                    <link rel="canonical" href="{{D}}">
                    <link rel="preload" as="image" href="/img/hero.png" imagesrcset="/img/hero.png 1x, /img/hero@2x.png 2x">
                    <style>
                      @import "/css/print.css";
                      .hero { background: url(img/hero.png); }
                    </style>
                    <script src="/js/site.js"></script>
                  </head>
                  <body>
                    <a href="{{D}}posts/first">First post</a>
                    <a href="posts/">All posts</a>
                    <a href="/about/#team">The team</a>
                    <a href="/blog/caf%C3%A9/">Café</a>
                    <a href="files/cv.pdf">CV</a>
                    <a href="#top">Top</a>
                    <a href="{{External}}">Elsewhere</a>
                    <map><area href="/about/" shape="rect" coords="0,0,1,1"></map>
                    <img src="/img/my%20photo.png" srcset="/img/my%20photo.png 1x, img/hero@2x.png 2x" alt="">
                    <picture><source srcset="/img/hero.webp" type="image/webp"><img src="/img/hero.png" alt=""></picture>
                    <video src="/media/clip.mp4" poster="/media/poster.jpg"><track src="/media/captions.vtt"></video>
                    <audio src="/media/sound.mp3"></audio>
                    <iframe src="/embed/"></iframe>
                    <div style="background-image: url('/img/hero.png')"></div>
                  </body>
                </html>
                """)
            .WithStylesheet("/css/site.css", """
                @import url("fonts.css");
                body { background: url(../img/paper.png); }
                """)
            .WithStylesheet("/css/fonts.css", "@font-face { src: url('/fonts/body.woff2') format('woff2'); }")
            .WithStylesheet("/css/print.css", "body { color: black; }")
            .WithText("/site.webmanifest", """
                { "icons": [ { "src": "/icons/192.png" }, { "src": "icons/512.png" } ] }
                """, "application/manifest+json")
            .WithRedirect("/posts/first", "/posts/first/")
            .WithPage("/posts/", Body("""<a href="first/">First</a> <a href="../">Home</a> <a href="/about/">About</a>"""))
            .WithPage("/posts/first/", Body("""
                <base href="/img/">
                <img src="hero.png" alt="">
                <a href="/posts/">All posts</a>
                <a href="../posts/first/#comments">Comments</a>
                """))
            .WithPage("/about/", Page("About", "../posts/first/", "/"))
            .WithPage("/blog/café/", Page("Café", "../../about/"))
            .WithPage("/embed/", Body("""<img src="../img/hero.png" alt="">"""))
            .WithFile("/favicon.ico", Png, "image/x-icon")
            .WithFile("/files/cv.pdf", Png, "application/pdf")
            .WithFile("/img/hero.png", Png, "image/png")
            .WithFile("/img/hero@2x.png", Png, "image/png")
            .WithFile("/img/hero.webp", Png, "image/webp")
            .WithFile("/img/my photo.png", Png, "image/png")
            .WithFile("/img/paper.png", Png, "image/png")
            .WithFile("/icons/192.png", Png, "image/png")
            .WithFile("/icons/512.png", Png, "image/png")
            .WithFile("/fonts/body.woff2", Png, "font/woff2")
            .WithFile("/media/clip.mp4", Png, "video/mp4")
            .WithFile("/media/poster.jpg", Png, "image/jpeg")
            .WithFile("/media/captions.vtt", Png, "text/vtt")
            .WithFile("/media/sound.mp3", Png, "audio/mpeg")
            .WithText("/js/site.js", "console.log('/not/a/link/');", "text/javascript");

        [Fact]
        public async Task EveryOnSiteLink_ResolvesToAFileInTheSnapshot()
        {
            var (site, run) = await ArchiveAsync(ConfigureFullSite);
            await using var _ = site;

            Assert.Empty(run.FailedAddresses);

            Uri liveSite = new(site.Domain);
            List<string> broken = new();
            int checkedLinks = 0;

            foreach (string path in run.Originals.Keys)
            {
                // As a browser would see the file, wherever the snapshot is
                // served from
                Uri address = new(SnapshotSite + string.Join("/", path.Split('/').Select(Uri.EscapeDataString)));
                string text = run.Text(path);

                Assert.DoesNotContain(liveSite.Authority, text);

                IEnumerable<Reference> references = Snapshot.MediaType(run.Files[path].ContentType) switch
                {
                    "text/html" => References.FromHtml(Parse(text), address),
                    "text/css" => References.FromCss(text, address),
                    _ => References.FromWebManifest(text, address),
                };

                foreach (Reference reference in references)
                {
                    if (reference.Address.Host != SnapshotHost)
                    {
                        Assert.Equal(new Uri(External).Host, reference.Address.Host);
                        continue;
                    }

                    checkedLinks++;
                    string target = reference.Address.AbsoluteUri;

                    // Azure decodes the address to get the blob's name
                    if (
                        !target.StartsWith(SnapshotSite, StringComparison.Ordinal) ||
                        !run.Files.ContainsKey(Uri.UnescapeDataString(target.Substring(SnapshotSite.Length)))
                    )
                    {
                        broken.Add($"{path} -> {reference.Address}");
                    }
                }
            }

            Assert.Empty(broken);
            Assert.True(checkedLinks >= 35, $"Only {checkedLinks} links were checked");
        }

        [Fact]
        public async Task EveryPage_IsReachableFromTheHomepageInTheSnapshot()
        {
            var (site, run) = await ArchiveAsync(ConfigureFullSite);
            await using var _ = site;

            HashSet<string> reached = new() { "index.html" };
            Queue<string> pages = new(reached);

            while (pages.TryDequeue(out string? path))
            {
                Uri address = new(SnapshotSite + string.Join("/", path.Split('/').Select(Uri.EscapeDataString)));

                foreach (Reference reference in References.FromHtml(Parse(run.Text(path)), address))
                {
                    string target = Uri.UnescapeDataString(reference.Address.AbsoluteUri.Replace(SnapshotSite, ""));

                    if (target.EndsWith(".html") && reached.Add(target))
                        pages.Enqueue(target);
                }
            }

            Assert.Equal(
                run.Paths.Where(path => path.EndsWith(".html")),
                reached.Order(StringComparer.Ordinal)
            );
        }

        [Theory]
        // Pages, by absolute, root-relative and relative address
        [InlineData("<a href=\"/about/\">", "<a href=\"../../about/index.html\">")]
        [InlineData("<a href=\"{{DOMAIN}}about/\">", "<a href=\"../../about/index.html\">")]
        [InlineData("<a href=\"../../about/\">", "<a href=\"../../about/index.html\">")]
        [InlineData("<a href=\"/sub/page/\">", "<a href=\"index.html\">")]
        [InlineData("<a href=\"{{DOMAIN}}\">", "<a href=\"../../index.html\">")]
        [InlineData("<area href=\"/about/\">", "<area href=\"../../about/index.html\">")]
        // Fragments are kept, and query strings dropped
        [InlineData("<a href=\"/about/#team\">", "<a href=\"../../about/index.html#team\">")]
        [InlineData("<a href=\"/about/?a=1&amp;b=2#x\">", "<a href=\"../../about/index.html#x\">")]
        [InlineData("<img src=\"/img/a.png?v=2\">", "<img src=\"../../img/a.png\">")]
        // Links that redirected point at the file they redirected to
        [InlineData("<a href=\"/post\">", "<a href=\"../../post/index.html\">")]
        // Files, and the attributes they're used from
        [InlineData("<a href=\"/files/cv.pdf\">", "<a href=\"../../files/cv.pdf\">")]
        [InlineData("<img src=\"/img/a.png\">", "<img src=\"../../img/a.png\">")]
        [InlineData("<img srcset=\"/img/a.png 1x, /img/b.png 2x\">", "<img srcset=\"../../img/a.png 1x, ../../img/b.png 2x\">")]
        [InlineData("<img srcset=\"data:image/png;base64,iVBO,Rw0 1x, /img/b.png 2x\">", "<img srcset=\"data:image/png;base64,iVBO,Rw0 1x, ../../img/b.png 2x\">")]
        [InlineData("<picture><source srcset=\"/img/a.png\"></picture>", "<picture><source srcset=\"../../img/a.png\"></picture>")]
        [InlineData("<video src=\"clip.mp4\" poster=\"/img/a.png\"></video>", "<video src=\"clip.mp4\" poster=\"../../img/a.png\"></video>")]
        [InlineData("<audio src=\"/sub/page/clip.mp4\"></audio>", "<audio src=\"clip.mp4\"></audio>")]
        [InlineData("<iframe src=\"/about/\"></iframe>", "<iframe src=\"../../about/index.html\"></iframe>")]
        [InlineData("<script src=\"/js/a.js\"></script>", "<script src=\"../../js/a.js\"></script>")]
        [InlineData("<link rel=\"stylesheet\" href=\"/css/a.css\">", "<link rel=\"stylesheet\" href=\"../../css/a.css\">")]
        [InlineData("<link rel=\"preload\" href=\"/img/a.png\" imagesrcset=\"/img/b.png 2x\">", "<link rel=\"preload\" href=\"../../img/a.png\" imagesrcset=\"../../img/b.png 2x\">")]
        [InlineData("<link rel=\"canonical\" href=\"{{DOMAIN}}about/\">", "<link rel=\"canonical\" href=\"../../about/index.html\">")]
        // CSS inside the page
        [InlineData("<div style=\"background: url('/img/a.png')\">", "<div style=\"background: url('../../img/a.png')\">")]
        [InlineData("<div style=\"background: url(&quot;/img/a.png&quot;)\">", "<div style=\"background: url(&quot;../../img/a.png&quot;)\">")]
        [InlineData("<style>@import \"/css/a.css\"; p { background: url(/img/a.png) }</style>", "<style>@import \"../../css/a.css\"; p { background: url(../../img/a.png) }</style>")]
        // The base address becomes the page itself, as every link is now
        // relative to it
        [InlineData("<base href=\"/img/\"><img src=\"a.png\">", "<base href=\"index.html\"><img src=\"../../img/a.png\">")]
        // Percent-encoded and non-ASCII addresses
        [InlineData("<img src=\"/img/my%20photo.png\">", "<img src=\"../../img/my%20photo.png\">")]
        [InlineData("<a href=\"/blog/caf%C3%A9/\">", "<a href=\"../../blog/caf%C3%A9/index.html\">")]
        [InlineData("<a href=\"/blog/café/\">", "<a href=\"../../blog/caf%C3%A9/index.html\">")]
        // Unquoted attributes are quoted, and quoting is kept
        [InlineData("<a href=/about/>", "<a href=\"../../about/index.html\">")]
        [InlineData("<a href='/about/'>", "<a href='../../about/index.html'>")]
        // Relative links to anything not in the snapshot go to the live site
        [InlineData("<a href=\"/missing/?q=1#x\">", "<a href=\"{{DOMAIN}}missing/?q=1#x\">")]
        [InlineData("<img src=\"missing.png\">", "<img src=\"{{DOMAIN}}sub/page/missing.png\">")]
        [InlineData("<link rel=\"alternate\" href=\"/index.xml\">", "<link rel=\"alternate\" href=\"{{DOMAIN}}index.xml\">")]
        // Everything else is left alone
        [InlineData("<a href=\"{{DOMAIN}}missing/\">", "<a href=\"{{DOMAIN}}missing/\">")]
        [InlineData("<a href=\"https://external.example/x?y=1&amp;z=2#f\">", "<a href=\"https://external.example/x?y=1&amp;z=2#f\">")]
        [InlineData("<img src=\"//cdn.invalid/a.png\">", "<img src=\"//cdn.invalid/a.png\">")]
        [InlineData("<a href=\"#top\">", "<a href=\"#top\">")]
        [InlineData("<a href=\"mailto:someone@example.com\">", "<a href=\"mailto:someone@example.com\">")]
        [InlineData("<a href=\"\">", "<a href=\"\">")]
        [InlineData("<a data-href=\"/about/\">", "<a data-href=\"/about/\">")]
        [InlineData("<p>/about/ &amp; <!-- <a href=\"/about/\"> --></p>", "<p>/about/ &amp; <!-- <a href=\"/about/\"> --></p>")]
        public async Task ReferenceInAPage_IsRewritten(string markup, string expected)
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", "/sub/page/", "/about/"))
                .WithPage("/sub/page/", Body(markup))
                .WithPage("/about/", Body("<p>About</p>"))
                .WithRedirect("/post", "/post/")
                .WithPage("/post/", Body("<p>Post</p>"))
                .WithPage("/blog/café/", Body("<p>Café</p>"))
                .WithFile("/sub/page/clip.mp4", Png, "video/mp4")
                .WithFile("/img/a.png", Png, "image/png")
                .WithFile("/img/b.png", Png, "image/png")
                .WithFile("/img/my photo.png", Png, "image/png")
                .WithFile("/files/cv.pdf", Png, "application/pdf")
                .WithFile("/js/a.js", Png, "text/javascript")
                .WithStylesheet("/css/a.css", "p { margin: 0; }")
            );
            await using var _ = site;

            Assert.Equal(
                Body(expected).Replace(D, site.Domain),
                run.Text("sub/page/index.html")
            );
        }

        [Fact]
        public async Task ReferencesInAStylesheet_AreRewrittenRelativeToIt()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Body("<link rel=\"stylesheet\" href=\"/css/main.css\">"))
                .WithStylesheet("/css/main.css", $$"""
                    @import "base.css";
                    @import url('/css/base.css') print;
                    /* url(/img/a.png) in a comment is left alone */
                    @font-face { src: url("/fonts/body.woff2#x") format("woff2"); }
                    .a { background: url( {{D}}img/a.png ); }
                    .b { background: url(/img/missing.png); }
                    .c { background: url(data:image/png;base64,iVBO=); }
                    .d { filter: url(#glow); background: url(https://cdn.invalid/b.png); }
                    """)
                .WithStylesheet("/css/base.css", "p { margin: 0; }")
                .WithFile("/fonts/body.woff2", Png, "font/woff2")
                .WithFile("/img/a.png", Png, "image/png")
            );
            await using var _ = site;

            Assert.Equal(
                $$"""
                @import "base.css";
                @import url('base.css') print;
                /* url(/img/a.png) in a comment is left alone */
                @font-face { src: url("../fonts/body.woff2#x") format("woff2"); }
                .a { background: url( ../img/a.png ); }
                .b { background: url({{site.Domain}}img/missing.png); }
                .c { background: url(data:image/png;base64,iVBO=); }
                .d { filter: url(#glow); background: url(https://cdn.invalid/b.png); }
                """,
                run.Text("css/main.css")
            );
        }

        [Fact]
        public async Task IconsInAWebManifest_AreRewrittenRelativeToIt()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Body("<link rel=\"manifest\" href=\"/app/site.webmanifest\">"))
                .WithText("/app/site.webmanifest", """
                    {
                      "name": "Example é",
                      "start_url": "/",
                      "icons": [
                        { "src": "/icons/192.png", "sizes": "192x192" },
                        { "src": "icons/512.png", "sizes": "512x512" },
                        { "src": "https://cdn.invalid/1024.png" }
                      ]
                    }
                    """, "application/manifest+json")
                .WithFile("/icons/192.png", Png, "image/png")
                .WithFile("/app/icons/512.png", Png, "image/png")
            );
            await using var _ = site;

            Assert.Equal(
                """
                {
                  "name": "Example é",
                  "start_url": "/",
                  "icons": [
                    { "src": "../icons/192.png", "sizes": "192x192" },
                    { "src": "icons/512.png", "sizes": "512x512" },
                    { "src": "https://cdn.invalid/1024.png" }
                  ]
                }
                """,
                run.Text("app/site.webmanifest")
            );
        }

        [Fact]
        public async Task RewrittenFiles_AreServedAsUtf8_AndOtherFilesAreUnchanged()
        {
            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Page("Home", "/files/cv.pdf", "/about/"))
                .WithFile("/about/", Encoding.UTF8.GetBytes(Body("<p>About</p>")), "text/html")
                .WithFile("/files/cv.pdf", Png, "application/pdf")
            );
            await using var _ = site;

            Assert.Equal("text/html; charset=utf-8", run.Files["index.html"].ContentType);
            Assert.Equal("text/html; charset=utf-8", run.Files["about/index.html"].ContentType);
            Assert.Equal("text/html", run.Originals["about/index.html"].ContentType);
            Assert.Equal("text/css; charset=utf-8", run.Files["css/site.css"].ContentType);
            Assert.Equal("application/pdf", run.Files["files/cv.pdf"].ContentType);
            Assert.Equal(Png, run.Files["files/cv.pdf"].Contents);
        }

        [Theory]
        [InlineData("<meta charset=\"windows-1252\">", "text/html", "<meta charset=\"utf-8\">")]
        [InlineData("<meta charset=\"iso-8859-1\">", "text/html", "<meta charset=\"utf-8\">")]
        [InlineData("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=windows-1252\">", "text/html", "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">")]
        [InlineData("<meta charset=\"utf-8\">", "text/html; charset=windows-1252", "<meta charset=\"utf-8\">")]
        [InlineData("", "text/html; charset=windows-1252", "")]
        public async Task PageInAnotherEncoding_IsRewrittenAsUtf8(string declaration, string contentType, string expectedDeclaration)
        {
            // The header's charset wins over the page's own declaration
            byte[] page = Windows1252($"<html><head>{declaration}</head><body><a href=\"/café/\">Café €</a></body></html>");

            var (site, run) = await ArchiveAsync(s => s
                .WithFile("/", page, contentType)
                .WithPage("/café/", Body("<p>Menu</p>"))
            );
            await using var _ = site;

            Assert.Empty(run.FailedAddresses);
            Assert.Equal("text/html; charset=utf-8", run.Files["index.html"].ContentType);
            Assert.Equal(
                $"<html><head>{expectedDeclaration}</head><body><a href=\"caf%C3%A9/index.html\">Café €</a></body></html>",
                run.Text("index.html")
            );
            Assert.Equal(page, run.Originals["index.html"].Contents);
        }

        [Fact]
        public async Task StylesheetInAnotherEncoding_IsRewrittenAsUtf8()
        {
            byte[] css = Windows1252("@charset \"iso-8859-1\";\n.a::before { content: \"é\"; background: url(/img/a.png); }");

            var (site, run) = await ArchiveAsync(s => s
                .WithPage("/", Body("<link rel=\"stylesheet\" href=\"/css/a.css\">"))
                .WithFile("/css/a.css", css, "text/css")
                .WithFile("/img/a.png", Png, "image/png")
            );
            await using var _ = site;

            Assert.Equal("text/css; charset=utf-8", run.Files["css/a.css"].ContentType);
            Assert.Equal(
                "@charset \"utf-8\";\n.a::before { content: \"é\"; background: url(../img/a.png); }",
                run.Text("css/a.css")
            );
            Assert.Equal(css, run.Originals["css/a.css"].Contents);
        }

        [Fact]
        public async Task PageWithoutLinksToRewrite_IsCopiedUnchanged()
        {
            string page = "<!doctype html>\n<HTML><Body CLASS=x>\r\n<p>Unclosed <b>tags &amp; odd &nbsp; markup<p>café</HTML>";

            var (site, run) = await ArchiveAsync(s => s.WithPage("/", page));
            await using var _ = site;

            Assert.Equal(run.Originals["index.html"].Contents, run.Files["index.html"].Contents);
        }

        [Fact]
        public async Task LinksInHtmlOutsideTheSiteRoot_AreRewrittenToo()
        {
            // The page at /other/ isn't crawled, as it's outside the root,
            // but it's captured because a page embeds it
            var (site, run) = await ArchiveAsync(
                s => s
                    .WithPage("/blog/", Body("<iframe src=\"/other/\"></iframe>"))
                    .WithPage("/other/", Body("<a href=\"/blog/\">Blog</a><img src=\"/img/a.png\">")),
                domain: root => root + "blog/"
            );
            await using var _ = site;

            Assert.Equal(
                Body($"<a href=\"../blog/index.html\">Blog</a><img src=\"{site.Domain}img/a.png\">"),
                run.Text("other/index.html")
            );
        }

        [Fact]
        public void RelativePath_ClimbsOnlyAsFarAsItNeeds()
        {
            Assert.Equal("b.css", LinkRewriter.RelativePath("css/a.css", "css/b.css"));
            Assert.Equal("../img/a.png", LinkRewriter.RelativePath("css/a.css", "img/a.png"));
            Assert.Equal("css/a.css", LinkRewriter.RelativePath("index.html", "css/a.css"));
            Assert.Equal("../../index.html", LinkRewriter.RelativePath("a/b/index.html", "index.html"));
            Assert.Equal("../c/index.html", LinkRewriter.RelativePath("a/b/index.html", "a/c/index.html"));
            Assert.Equal("index.html", LinkRewriter.RelativePath("a/index.html", "a/index.html"));
            Assert.Equal("a/index.html", LinkRewriter.RelativePath("index.html", "a/index.html"));
            Assert.Equal("odd%252Fname.txt", LinkRewriter.RelativePath("index.html", "odd%2Fname.txt"));
            Assert.Equal("my%20photo.png", LinkRewriter.RelativePath("index.html", "my photo.png"));
            Assert.Equal("%3Ab.png", LinkRewriter.RelativePath("index.html", ":b.png"));
        }

        private static HtmlDocument Parse(string html)
        {
            HtmlDocument document = new();
            document.LoadHtml(html);
            return document;
        }
    }
}
