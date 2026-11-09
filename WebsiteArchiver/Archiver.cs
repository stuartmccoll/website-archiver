using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace WebsiteArchiver
{
    public class Archiver
    {

        private static readonly HttpClient httpClient = new();

        private AzureBlobStorageClient blobStorageClient;
        private HtmlWeb web;
        private HashSet<String> visitedLinks;
        private List<String> failedAddresses;
        private Uri rootUri;

        /// <exception cref="InvalidOperationException">DOMAIN isn't set to
        /// an absolute http or https URL.</exception>
        public Archiver()
        {
            // Check DOMAIN first, so a missing value is reported clearly
            // rather than as a storage configuration error
            this.rootUri = GetRootUri();
            this.blobStorageClient = new();
            this.web = new HtmlWeb();
            this.visitedLinks = new HashSet<String>();
            this.failedAddresses = new List<String>();
        }

        /// <summary>
        /// Addresses of pages and stylesheets that couldn't be fetched
        /// during the last crawl. These are skipped rather than archived.
        /// </summary>
        public IReadOnlyList<String> FailedAddresses => this.failedAddresses;

        /// <summary>
        /// Main method responsible for orchestrating crawling and storage
        /// of all applicable pages.
        /// </summary>
        /// <returns>An awaitable Task.</returns>
        /// <exception cref="InvalidOperationException">The homepage couldn't
        /// be fetched, so there is nothing to archive.</exception>
        async public Task Crawl()
        {
            // Links back to the homepage shouldn't fetch or store it again
            this.visitedLinks.Add(this.rootUri.AbsoluteUri);

            HtmlDocument htmlDoc = this.LoadPage(this.rootUri, out Uri homepageUri);

            if (htmlDoc == null)
                throw new InvalidOperationException($"Couldn't fetch the homepage at {this.rootUri}");

            await this.CrawlCascadingStyleSheet(htmlDoc, homepageUri);

            await this.StoreDocument(htmlDoc, "main");

            await this.CrawlLinks(htmlDoc, homepageUri);
        }

        /// <summary>
        /// Reads the DOMAIN environment variable, ensuring it ends with a
        /// trailing slash so that relative links resolve beneath it.
        /// </summary>
        /// <returns>The root URL of the site being archived.</returns>
        private static Uri GetRootUri()
        {
            string domain = Environment.GetEnvironmentVariable("DOMAIN");

            if (
                !Uri.TryCreate(domain, UriKind.Absolute, out Uri parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            )
            {
                throw new InvalidOperationException(
                    $"The DOMAIN environment variable must be set to the site to archive, e.g. https://example.com/ (it is currently '{domain}')"
                );
            }

            if (!domain.EndsWith("/"))
                domain += "/";

            return new Uri(domain);
        }

        /// <summary>
        /// Crawls any cascading stylesheets on the current domain linked
        /// from the given HTML document and orchestrates their storage in
        /// Azure Blob Storage. Stylesheets hosted elsewhere, such as on a
        /// CDN, are skipped.
        /// </summary>
        /// <param name="htmlDoc">An HTML document.</param>
        /// <param name="pageUri">The address of the HTML document, used to
        /// resolve relative stylesheet links.</param>
        /// <returns>An awaitable Task.</returns>
        async private Task CrawlCascadingStyleSheet(HtmlDocument htmlDoc, Uri pageUri)
        {
            HtmlNodeCollection stylesheets = htmlDoc.DocumentNode.SelectNodes(
                "//link[@rel='stylesheet'][@href]"
            );

            // SelectNodes returns null, not an empty collection, when
            // nothing matches
            if (stylesheets == null)
                return;

            foreach (HtmlNode stylesheet in stylesheets)
            {
                Uri address = this.ResolveLink(pageUri, stylesheet.Attributes["href"].Value);

                if (address == null || !this.IsOnSite(address))
                {
                    Console.WriteLine($"Skipping off-site stylesheet {stylesheet.Attributes["href"].Value}");
                    continue;
                }

                string fileName = address.AbsolutePath
                    .Split("/")
                    .LastOrDefault((value) => value != "");

                if (fileName == null)
                    continue;

                Console.WriteLine($"Storing {fileName}");

                string fileContents;

                try
                {
                    fileContents = await httpClient.GetStringAsync(address);
                }
                catch (Exception exception) when (
                    exception is HttpRequestException || exception is TaskCanceledException
                )
                {
                    this.RecordFailure(address, exception.Message);
                    continue;
                }

                await this.blobStorageClient.UploadBlob(fileName, "", fileContents);
            }
        }

        /// <summary>
        /// Parses the current document and builds a list of all links
        /// (anchor tags with an href attribute) within the page that should
        /// be crawled, resolved to absolute addresses. Links are excluded
        /// if they are:
        /// <list type="bullet">
        /// <item>
        /// <description>Not on the current domain.</description>
        /// </item>
        /// <item>
        /// <description>Anything that can't be parsed as HTML, such as XML
        /// files.</description>
        /// </item>
        /// <item>
        /// <description>Fragment-only links (e.g. #top) to a part of the
        /// current page.</description>
        /// </item>
        /// </list>
        /// </summary>
        /// <param name="htmlDoc">The current document</param>
        /// <param name="pageUri">The address of the current document, used
        /// to resolve relative links.</param>
        /// <returns>A list of links to crawl, which is empty if the page has
        /// none.</returns>
        private List<Uri> GetAllLinks(HtmlDocument htmlDoc, Uri pageUri)
        {
            List<Uri> links = new List<Uri>();

            HtmlNodeCollection anchors = htmlDoc.DocumentNode.SelectNodes("//a[@href]");

            // SelectNodes returns null, not an empty collection, when
            // nothing matches
            if (anchors == null)
                return links;

            foreach (HtmlNode anchor in anchors)
            {
                string href = anchor.Attributes["href"].Value.Trim();

                if (href.StartsWith("#"))
                    continue;

                Uri link = this.ResolveLink(pageUri, href);

                if (
                    link != null &&
                    this.IsOnSite(link) &&
                    !link.AbsolutePath.EndsWith(".xml")
                )
                {
                    links.Add(link);
                }
            }

            return links;
        }

        /// <summary>
        /// Retrieves all links in a given HTML page and orchestrates
        /// crawling and storage of each.
        /// </summary>
        /// <param name="htmlDoc">An HTML document.</param>
        /// <param name="pageUri">The address of the HTML document.</param>
        /// <returns>An awaitable Task.</returns>
        private async Task CrawlLinks(HtmlDocument htmlDoc, Uri pageUri)
        {
            List<Uri> links = this.GetAllLinks(htmlDoc, pageUri);

            foreach (Uri link in links)
            {
                await this.CrawlLink(link);
            }
        }

        /// <summary>
        /// Resolves a link, which may be absolute or relative, against the
        /// address of the page it was found on. Any query string or fragment
        /// (e.g. ?utm_source=nav or #top) is removed, so that each page is
        /// fetched and stored once under a name derived from its path.
        /// </summary>
        /// <param name="pageUri">The address of the page containing the
        /// link.</param>
        /// <param name="href">The link's href attribute value.</param>
        /// <returns>The absolute address of the link, or null if it can't be
        /// parsed.</returns>
        private Uri ResolveLink(Uri pageUri, string href)
        {
            if (!Uri.TryCreate(pageUri, HtmlEntity.DeEntitize(href).Trim(), out Uri link))
                return null;

            return new Uri(link.GetLeftPart(UriPartial.Path));
        }

        /// <summary>
        /// Determines whether an address belongs to the site being archived:
        /// same scheme, host and port as DOMAIN, and beneath its path.
        /// </summary>
        /// <param name="address">An absolute address.</param>
        /// <returns>True if the address is on the site being
        /// archived.</returns>
        private bool IsOnSite(Uri address)
        {
            return address.Scheme == this.rootUri.Scheme &&
                String.Equals(
                    address.Authority,
                    this.rootUri.Authority,
                    StringComparison.OrdinalIgnoreCase
                ) &&
                address.AbsolutePath.StartsWith(this.rootUri.AbsolutePath, StringComparison.Ordinal);
        }

        /// <summary>
        /// Visits a given link if it hasn't seen it before, handing the
        /// document off for storage and continuing to crawl any further
        /// links found. If the link redirects, as GitHub Pages does from
        /// /post to /post/, the page is stored and crawled under the address
        /// it redirected to, so it isn't archived twice and its relative
        /// links resolve correctly.
        /// </summary>
        /// <param name="link">The absolute address of a page on the
        /// site.</param>
        /// <returns>An awaitable Task.</returns>
        private async Task CrawlLink(Uri link)
        {
            String address = link.AbsoluteUri;

            if (this.visitedLinks.Contains(address))
            {
                Console.WriteLine($"Already visited {address}");
            }
            else
            {
                this.visitedLinks.Add(address);

                HtmlDocument htmlDoc = this.LoadPage(link, out Uri pageUri);

                // Don't archive error pages, or follow their links
                if (htmlDoc == null)
                    return;

                if (pageUri.AbsoluteUri != address)
                {
                    if (!this.IsOnSite(pageUri))
                    {
                        Console.WriteLine($"Skipping {address}, which redirects off-site to {pageUri}");
                        return;
                    }

                    if (!this.visitedLinks.Add(pageUri.AbsoluteUri))
                    {
                        Console.WriteLine($"Already visited {pageUri} (redirected from {address})");
                        return;
                    }
                }

                await this.StoreDocument(htmlDoc, this.GetPathRelativeToRoot(pageUri));

                Console.WriteLine($"Crawling {pageUri}");

                await this.CrawlLinks(htmlDoc, pageUri);
            }
        }

        /// <summary>
        /// Fetches and parses a page.
        /// </summary>
        /// <param name="address">The address of the page.</param>
        /// <param name="finalAddress">The address the page was actually
        /// served from, after following any redirects, without a query
        /// string or fragment.</param>
        /// <returns>The parsed page, or null if it couldn't be fetched or
        /// the server returned an unsuccessful status code. Failures are
        /// recorded in <see cref="FailedAddresses"/>.</returns>
        private HtmlDocument LoadPage(Uri address, out Uri finalAddress)
        {
            HtmlDocument htmlDoc;
            finalAddress = address;

            try
            {
                htmlDoc = this.web.Load(address.AbsoluteUri);
            }
            catch (Exception exception)
            {
                this.RecordFailure(address, exception.Message);
                return null;
            }

            int statusCode = (int)this.web.StatusCode;

            if (statusCode < 200 || statusCode > 299)
            {
                this.RecordFailure(address, $"HTTP status {statusCode}");
                return null;
            }

            if (this.web.ResponseUri != null)
                finalAddress = new Uri(this.web.ResponseUri.GetLeftPart(UriPartial.Path));

            return htmlDoc;
        }

        /// <summary>
        /// Logs that an address couldn't be fetched and adds it to
        /// <see cref="FailedAddresses"/>.
        /// </summary>
        /// <param name="address">The address that couldn't be
        /// fetched.</param>
        /// <param name="reason">Why it couldn't be fetched.</param>
        private void RecordFailure(Uri address, String reason)
        {
            Console.Error.WriteLine($"Failed to fetch {address}: {reason}");
            this.failedAddresses.Add(address.AbsoluteUri);
        }

        /// <summary>
        /// Gets the path of an on-site address relative to DOMAIN, e.g.
        /// https://example.com/blog/post/ becomes blog/post/.
        /// </summary>
        /// <param name="link">An absolute address on the site.</param>
        /// <returns>The address relative to DOMAIN.</returns>
        private String GetPathRelativeToRoot(Uri link)
        {
            return link.AbsolutePath.Substring(this.rootUri.AbsolutePath.Length);
        }

        /// <summary>
        /// Orchestrates storage of a document in Azure Blob Storage.
        /// </summary>
        /// <param name="htmlDoc">An HTML document.</param>
        /// <param name="relativePath">The address of the HTML document
        /// relative to DOMAIN, e.g. blog/post/.</param>
        /// <returns>An awaitable Task.</returns>
        private async Task StoreDocument(HtmlDocument htmlDoc, String relativePath)
        {
            string fileName = relativePath.TrimEnd('/');

            string lastSegment = fileName.Substring(fileName.LastIndexOf('/') + 1);

            // Pages are stored as .html, unless their address already ends
            // in .html or .htm, e.g. page.html. Other periods in the name,
            // as in python-3.7-data-classes, aren't treated as extensions.
            bool hasHtmlExtension =
                lastSegment.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                lastSegment.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

            string fileExtension = hasHtmlExtension ? "" : "html";

            await this.blobStorageClient.UploadBlob(fileName, fileExtension, htmlDoc.Text);
        }
    }
}
