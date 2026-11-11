using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace WebsiteArchiver
{
    /// <summary>
    /// Crawls the site at DOMAIN into memory, then publishes everything it
    /// captured as a snapshot.
    /// </summary>
    public class Archiver
    {
        // Every file is held in memory until the snapshot is published, so
        // one very large file can't exhaust it
        private const long MaxFileSize = 100 * 1024 * 1024;

        private static readonly HttpClient httpClient = CreateHttpClient();

        private readonly Uri domainUri;
        private readonly AzureBlobStorageClient blobStorageClient;
        private readonly List<string> failedAddresses = new();
        private readonly List<CapturedFile> files = new();
        private readonly Dictionary<string, CapturedFile> filesByPath = new(StringComparer.Ordinal);

        // Addresses that weren't stored because another address had
        // already been stored at the same path, and that path
        private readonly Dictionary<string, string> duplicatePaths = new();
        private readonly List<SnapshotRedirect> redirects = new();
        private readonly Queue<Reference> queue = new();

        // Addresses that have been queued, and addresses that have been
        // fetched (or reached by a redirect), so each is fetched once
        private readonly HashSet<string> queued = new();
        private readonly HashSet<string> fetched = new();

        private Uri rootUri;

        /// <summary>
        /// Reads the configuration from environment variables.
        /// </summary>
        /// <exception cref="InvalidOperationException">DOMAIN isn't set to
        /// an absolute http or https URL.</exception>
        public Archiver()
        {
            // Check DOMAIN first, so a missing value is reported clearly
            // rather than as a storage configuration error
            this.domainUri = GetDomainUri();
            this.rootUri = this.domainUri;
            this.blobStorageClient = new();
        }

        /// <summary>
        /// Addresses that couldn't be fetched during the last crawl. These
        /// are skipped rather than archived, and the snapshot is marked
        /// incomplete.
        /// </summary>
        public IReadOnlyList<string> FailedAddresses => this.failedAddresses;

        /// <summary>
        /// The ID of the snapshot the last crawl published, e.g.
        /// 2026-11-10T09-15-00Z, with -2 or similar added if another run
        /// started in the same second.
        /// </summary>
        public string SnapshotId { get; private set; }

        /// <summary>
        /// Crawls the site, starting at its homepage, then publishes the
        /// snapshot. Pages beneath the site root are crawled for links, and
        /// every file they use on the same host is captured.
        /// </summary>
        /// <returns>An awaitable Task.</returns>
        /// <exception cref="InvalidOperationException">The homepage couldn't
        /// be fetched, so there is nothing to archive.</exception>
        async public Task Crawl()
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            this.SnapshotId = Snapshot.CreateId(startedAt);

            Reference homepageReference = new(this.domainUri, ReferenceKind.Page);
            this.queued.Add(this.domainUri.AbsoluteUri);
            this.fetched.Add(this.domainUri.AbsoluteUri);

            FetchedResponse homepage = await this.Fetch(this.domainUri);

            if (homepage == null)
                throw new InvalidOperationException($"Couldn't fetch the homepage at {this.domainUri}");

            // If DOMAIN redirects, e.g. from http to https, the site's
            // links point at where it redirects to
            this.rootUri = new Uri(homepage.Address, ".");

            if (this.rootUri.AbsoluteUri != this.domainUri.AbsoluteUri)
                Console.WriteLine($"{this.domainUri} redirects to {homepage.Address}, so crawling beneath {this.rootUri}");

            CapturedFile homepageFile = this.Capture(homepageReference, homepage);

            while (this.queue.TryDequeue(out Reference reference))
            {
                if (!this.fetched.Add(reference.Address.AbsoluteUri))
                    continue;

                FetchedResponse response = await this.Fetch(reference.Address);

                if (response != null)
                    this.Capture(reference, response);
            }

            this.RewriteLinks();

            SnapshotManifest manifest = new()
            {
                Id = this.SnapshotId,
                Domain = this.domainUri.AbsoluteUri,
                Root = this.rootUri.AbsoluteUri,
                Homepage = homepageFile?.Path,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                Status = this.failedAddresses.Count == 0 ? "complete" : "incomplete",
                FailedAddresses = this.failedAddresses.ToList(),
                Redirects = this.redirects.ToList(),
                Files = this.files.Select(Snapshot.Describe).ToList(),
            };

            this.SnapshotId = await Snapshot.Publish(this.blobStorageClient, manifest, this.files);

            Console.WriteLine($"Published snapshot {this.SnapshotId} with {this.files.Count} file(s)");
        }

        /// <summary>
        /// Reads the DOMAIN environment variable, ensuring it ends with a
        /// trailing slash so that relative links resolve beneath it.
        /// </summary>
        /// <returns>The address the crawl starts from.</returns>
        private static Uri GetDomainUri()
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
        /// Creates the HttpClient every request is made with. It follows
        /// redirects, decompresses responses and gives up on a file after a
        /// minute, or if it's larger than <see cref="MaxFileSize"/>.
        /// </summary>
        /// <returns>The HttpClient.</returns>
        private static HttpClient CreateHttpClient()
        {
            SocketsHttpHandler handler = new()
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.All,
            };

            HttpClient client = new(handler)
            {
                Timeout = TimeSpan.FromMinutes(1),
                MaxResponseContentBufferSize = MaxFileSize,
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("WebsiteArchiver");

            return client;
        }

        /// <summary>
        /// Fetches a file, following any redirects.
        /// </summary>
        /// <param name="address">The file's address.</param>
        /// <returns>The response, or null if it couldn't be fetched or the
        /// server returned an unsuccessful status code. Failures are
        /// recorded in <see cref="FailedAddresses"/>.</returns>
        private async Task<FetchedResponse> Fetch(Uri address)
        {
            Console.WriteLine($"Fetching {address}");

            try
            {
                using HttpResponseMessage response = await httpClient.GetAsync(address);

                if (!response.IsSuccessStatusCode)
                {
                    this.RecordFailure(address, $"HTTP status {(int)response.StatusCode}");
                    return null;
                }

                return new FetchedResponse(
                    new Uri(response.RequestMessage.RequestUri.GetLeftPart(UriPartial.Path)),
                    await response.Content.ReadAsByteArrayAsync(),
                    response.Content.Headers.ContentType?.ToString()
                );
            }
            catch (Exception exception) when (
                exception is HttpRequestException || exception is TaskCanceledException
            )
            {
                this.RecordFailure(address, exception.Message);
                return null;
            }
        }

        /// <summary>
        /// Adds a fetched file to the snapshot and queues whatever it
        /// refers to. How the file is read depends on its Content-Type:
        /// <list type="bullet">
        /// <item><description>HTML beneath the site root is crawled for
        /// links and the files it uses.</description></item>
        /// <item><description>CSS is read for url(…) and
        /// @import.</description></item>
        /// <item><description>A web app manifest is read for its
        /// icons.</description></item>
        /// <item><description>Anything else is stored as it
        /// is.</description></item>
        /// </list>
        /// If the request redirected, as GitHub Pages does from /post to
        /// /post/, the file is stored and read under the address it
        /// redirected to, so it isn't archived twice and its relative links
        /// resolve correctly.
        /// </summary>
        /// <param name="reference">What was requested.</param>
        /// <param name="response">The response.</param>
        /// <returns>The captured file, or null if it was skipped.</returns>
        private CapturedFile Capture(Reference reference, FetchedResponse response)
        {
            Uri address = response.Address;

            if (address.AbsoluteUri != reference.Address.AbsoluteUri)
            {
                if (reference.Kind == ReferenceKind.Page ? !this.IsUnderRoot(address) : !this.IsOnHost(address))
                {
                    Console.WriteLine($"Skipping {reference.Address}, which redirects off-site to {address}");
                    return null;
                }

                this.redirects.Add(new SnapshotRedirect(reference.Address.AbsoluteUri, address.AbsoluteUri));

                if (!this.fetched.Add(address.AbsoluteUri))
                {
                    Console.WriteLine($"Already fetched {address} (redirected from {reference.Address})");
                    return null;
                }
            }

            string contentType = response.ContentType ?? "application/octet-stream";
            string mediaType = Snapshot.MediaType(contentType);
            bool isHtml = Snapshot.IsHtml(contentType);
            bool isCss = mediaType == "text/css" || (reference.Kind == ReferenceKind.Stylesheet && !isHtml);
            bool isWebManifest =
                mediaType == "application/manifest+json" ||
                (reference.Kind == ReferenceKind.WebManifest && !isHtml);

            CapturedFormat format =
                isHtml ? CapturedFormat.Html :
                isCss ? CapturedFormat.Css :
                isWebManifest ? CapturedFormat.WebManifest :
                CapturedFormat.Other;

            string path = SnapshotPaths.FromAddress(address, isHtml);

            if (this.filesByPath.TryGetValue(path, out CapturedFile existing))
            {
                Console.WriteLine($"Skipping {address}, because {existing.Address} is already stored as {path}");
                this.duplicatePaths[address.AbsoluteUri] = path;
                return null;
            }

            CapturedFile file = new(address, path, response.Content, contentType, format);
            this.files.Add(file);
            this.filesByPath.Add(path, file);

            List<Reference> references;

            if (isHtml && this.IsUnderRoot(address))
            {
                HtmlDocument html = new();
                html.LoadHtml(TextDecoding.Decode(response.Content, contentType, format));
                references = References.FromHtml(html, address);
            }
            else if (isCss)
            {
                references = References.FromCss(TextDecoding.Decode(response.Content, contentType, format), address);
            }
            else if (isWebManifest)
            {
                references = References.FromWebManifest(TextDecoding.Decode(response.Content, contentType, format), address);
            }
            else
            {
                references = new();
            }

            foreach (Reference found in references)
                this.Follow(found);

            return file;
        }

        /// <summary>
        /// Queues a reference to be fetched, if it's on the site and hasn't
        /// been queued before. Links to pages must be beneath the site
        /// root. Files a page uses only need to be on the same host, so a
        /// site at example.com/blog/ can use /css/site.css.
        /// </summary>
        /// <param name="reference">A reference found in a captured
        /// file.</param>
        private void Follow(Reference reference)
        {
            bool onSite = reference.Kind == ReferenceKind.Page
                ? this.IsUnderRoot(reference.Address)
                : this.IsOnHost(reference.Address);

            if (!onSite)
            {
                if (reference.Kind != ReferenceKind.Page)
                    Console.WriteLine($"Skipping off-site file {reference.Address}");

                return;
            }

            if (this.queued.Add(reference.Address.AbsoluteUri))
                this.queue.Enqueue(reference);
        }

        /// <summary>
        /// Determines whether an address is on the same scheme, host and
        /// port as the site root.
        /// </summary>
        /// <param name="address">An absolute address.</param>
        /// <returns>True if the address is on the site's host.</returns>
        private bool IsOnHost(Uri address)
        {
            return address.Scheme == this.rootUri.Scheme &&
                String.Equals(address.Authority, this.rootUri.Authority, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether an address is on the site's host and beneath
        /// the site root's path.
        /// </summary>
        /// <param name="address">An absolute address.</param>
        /// <returns>True if the address is beneath the site root.</returns>
        private bool IsUnderRoot(Uri address)
        {
            return this.IsOnHost(address) &&
                address.AbsolutePath.StartsWith(this.rootUri.AbsolutePath, StringComparison.Ordinal);
        }

        /// <summary>
        /// Rewrites the links in every captured HTML page, stylesheet and
        /// web manifest, so the snapshot can be browsed on its own. The
        /// originals are kept as they were. If a file can't be rewritten,
        /// its original is used in site/ as well.
        /// </summary>
        private void RewriteLinks()
        {
            Dictionary<string, string> pathsByAddress = new(this.duplicatePaths);

            foreach (CapturedFile file in this.files)
                pathsByAddress[file.Address.AbsoluteUri] = file.Path;

            // Point links that redirected, e.g. from /post to /post/, at
            // the file they redirected to
            foreach (SnapshotRedirect redirect in this.redirects)
            {
                if (pathsByAddress.TryGetValue(redirect.To, out string path))
                    pathsByAddress.TryAdd(redirect.From, path);
            }

            LinkRewriter rewriter = new(pathsByAddress);

            for (int i = 0; i < this.files.Count; i++)
            {
                try
                {
                    this.files[i] = rewriter.Rewrite(this.files[i]);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Couldn't rewrite the links in {this.files[i].Address}, so it's stored unchanged: {exception.Message}");
                }
            }
        }

        /// <summary>
        /// Logs that an address couldn't be fetched and adds it to
        /// <see cref="FailedAddresses"/>.
        /// </summary>
        /// <param name="address">The address that couldn't be
        /// fetched.</param>
        /// <param name="reason">Why it couldn't be fetched.</param>
        private void RecordFailure(Uri address, string reason)
        {
            Console.Error.WriteLine($"Failed to fetch {address}: {reason}");
            this.failedAddresses.Add(address.AbsoluteUri);
        }

        /// <summary>
        /// A successful response.
        /// </summary>
        /// <param name="Address">The address the response came from, after
        /// any redirects, without a query string or fragment.</param>
        /// <param name="Content">The response body.</param>
        /// <param name="ContentType">The response's Content-Type, or null if
        /// it didn't have one.</param>
        private record FetchedResponse(Uri Address, byte[] Content, string ContentType);
    }
}
