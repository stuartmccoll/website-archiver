using System;
using System.Linq;

namespace WebsiteArchiver
{
    /// <summary>
    /// Maps the address of a captured file to its path inside a snapshot.
    /// Paths mirror the site's URLs, relative to the host's root, so
    /// https://example.com/posts/x/ becomes posts/x/index.html and
    /// https://example.com/css/site.css stays css/site.css.
    /// </summary>
    public static class SnapshotPaths
    {
        /// <summary>
        /// Gets the path a file is stored under inside a snapshot.
        /// <list type="bullet">
        /// <item><description>Addresses ending in / get index.html
        /// appended.</description></item>
        /// <item><description>HTML pages whose address doesn't end in .html
        /// or .htm, such as /about served without a redirect, are stored
        /// as about/index.html. Periods elsewhere in the name, as in
        /// python-3.7-data-classes, aren't treated as
        /// extensions.</description></item>
        /// <item><description>Everything else keeps its name, e.g.
        /// files/cv.pdf.</description></item>
        /// </list>
        /// Percent-encoded characters are decoded, so /blog/caf%C3%A9/
        /// becomes blog/café/index.html. A segment is left encoded if
        /// decoding it would change the folder structure, e.g. %2F.
        /// </summary>
        /// <param name="address">The file's absolute address.</param>
        /// <param name="isHtml">Whether the file was served as
        /// HTML.</param>
        /// <returns>The path of the file inside the snapshot, with no
        /// leading slash.</returns>
        public static string FromAddress(Uri address, bool isHtml)
        {
            string[] segments = address.AbsolutePath
                .TrimStart('/')
                .Split('/')
                .Select(DecodeSegment)
                .ToArray();

            string path = String.Join("/", segments);
            string lastSegment = segments[^1];

            if (lastSegment.Length == 0)
                return path + "index.html";

            bool hasHtmlExtension =
                lastSegment.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                lastSegment.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

            if (isHtml && !hasHtmlExtension)
                return path + "/index.html";

            return path;
        }

        /// <summary>
        /// Decodes one percent-encoded path segment, unless the decoded
        /// form would be read as a different folder structure.
        /// </summary>
        /// <param name="segment">A segment of a URL path, as it appears
        /// in the URL.</param>
        /// <returns>The decoded segment, or the original if decoding it
        /// isn't safe.</returns>
        private static string DecodeSegment(string segment)
        {
            string decoded = Uri.UnescapeDataString(segment);

            bool changesStructure =
                decoded.Contains('/') ||
                decoded.Contains('\\') ||
                decoded == "." ||
                decoded == "..";

            return changesStructure ? segment : decoded;
        }
    }
}
