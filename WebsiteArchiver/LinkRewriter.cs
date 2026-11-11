using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace WebsiteArchiver
{
    /// <summary>
    /// Rewrites the links in a snapshot's HTML, CSS and web manifests so
    /// the snapshot can be browsed on its own, from any address:
    /// <list type="bullet">
    /// <item><description>A reference to a file in the snapshot becomes a
    /// path relative to the file it's in, e.g. ../css/site.css, keeping
    /// any fragment. A reference to an address that redirected points at
    /// the file it redirected to.</description></item>
    /// <item><description>A relative reference to anything else, such as a
    /// page that couldn't be fetched, becomes an absolute link to the live
    /// site.</description></item>
    /// <item><description>Absolute references to anything else, including
    /// every off-site link, are left as they are.</description></item>
    /// </list>
    /// The rewritten copy is UTF-8, and its charset declarations say
    /// so.
    /// </summary>
    public sealed class LinkRewriter
    {
        // A scheme, as in https:, or a scheme-relative address, as in
        // //example.com/
        private static readonly Regex AbsoluteReference = new(@"^(?:[a-zA-Z][a-zA-Z0-9+.\-]*:|[/\\]{2})");

        private static readonly Regex ContentCharset = new(@"charset\s*=\s*[""']?[^\s;""']*[""']?", RegexOptions.IgnoreCase);

        private static readonly Regex CssCharsetRule = new(@"^@charset ""(?<charset>[^""]*)"";");

        private readonly IReadOnlyDictionary<string, string> pathsByAddress;

        /// <param name="pathsByAddress">The path in the snapshot of each
        /// captured address, without its query string or fragment,
        /// including addresses that redirected to a captured
        /// file.</param>
        public LinkRewriter(IReadOnlyDictionary<string, string> pathsByAddress)
        {
            this.pathsByAddress = pathsByAddress;
        }

        /// <summary>
        /// Rewrites a captured file's links, if it's HTML, CSS or a web
        /// manifest.
        /// </summary>
        /// <param name="file">A captured file.</param>
        /// <returns>The file with its rewritten copy set, or the file as
        /// it was if it has no links to rewrite.</returns>
        public CapturedFile Rewrite(CapturedFile file)
        {
            if (file.Format == CapturedFormat.Other)
                return file;

            string text = TextDecoding.Decode(file.Content, file.ContentType, file.Format);
            ReferenceMapper map = (value, baseAddress, kind) => this.Map(value, baseAddress, file.Path);

            string rewritten = file.Format switch
            {
                CapturedFormat.Html => this.RewriteHtml(text, file, map),
                CapturedFormat.Css => RewriteCssCharset(References.MapCss(text, file.Address, map)),
                // A manifest that isn't valid JSON is copied as it is
                _ => References.MapWebManifest(text, file.Address, map) ?? text,
            };

            return file with
            {
                SiteContent = Encoding.UTF8.GetBytes(rewritten),
                SiteContentType = TextDecoding.WithUtf8Charset(file.ContentType),
            };
        }

        /// <summary>
        /// Gets the path from one file in a snapshot to another, with each
        /// segment percent-encoded, e.g. ../css/site.css.
        /// </summary>
        /// <param name="fromPath">The path of the file the link is
        /// in.</param>
        /// <param name="toPath">The path of the file it links to.</param>
        /// <returns>The relative path.</returns>
        public static string RelativePath(string fromPath, string toPath)
        {
            string[] fromFolders = fromPath.Split('/')[..^1];
            string[] toSegments = toPath.Split('/');
            int common = 0;

            while (
                common < fromFolders.Length &&
                common < toSegments.Length - 1 &&
                fromFolders[common] == toSegments[common]
            )
            {
                common++;
            }

            return String.Join(
                "/",
                Enumerable.Repeat("..", fromFolders.Length - common)
                    .Concat(toSegments.Skip(common).Select(Uri.EscapeDataString))
            );
        }

        /// <summary>
        /// Works out what a reference should be replaced with.
        /// </summary>
        /// <param name="value">The reference, as written.</param>
        /// <param name="baseAddress">The address it resolves
        /// against.</param>
        /// <param name="fromPath">The path of the file it's in.</param>
        /// <returns>Its replacement, which may be the same.</returns>
        private string Map(string value, Uri baseAddress, string fromPath)
        {
            if (value.Length == 0)
                return value;

            Uri address = References.Resolve(baseAddress, value);

            if (address == null)
                return value;

            if (this.pathsByAddress.TryGetValue(address.AbsoluteUri, out string toPath))
            {
                int fragment = value.IndexOf('#');
                return RelativePath(fromPath, toPath) + (fragment >= 0 ? value[fragment..] : "");
            }

            if (AbsoluteReference.IsMatch(value))
                return value;

            // A relative link to something that isn't in the snapshot
            // would be broken there, so link to the live site instead
            return new Uri(baseAddress, value).AbsoluteUri;
        }

        /// <summary>
        /// Rewrites a page's links, its base href and its charset
        /// declarations.
        /// </summary>
        /// <param name="text">The page's source.</param>
        /// <param name="file">The page.</param>
        /// <param name="map">Rewrites each reference.</param>
        /// <returns>The rewritten page.</returns>
        private string RewriteHtml(string text, CapturedFile file, ReferenceMapper map)
        {
            HtmlDocument html = new();
            html.LoadHtml(text);

            List<TextEdit> edits = References.MapHtml(html, file.Address, map);

            // Every rewritten link is relative to the page itself, so the
            // base address must be too
            if (References.GetBaseElement(html) is HtmlNode baseElement)
                edits.Add(TextEdit.ForAttribute(text, baseElement.Attributes["href"], Uri.EscapeDataString(file.Path.Split('/')[^1])));

            foreach (HtmlNode meta in html.DocumentNode.Descendants("meta"))
            {
                HtmlAttribute charset = meta.Attributes["charset"];
                HtmlAttribute content = meta.Attributes["content"];

                if (charset != null && !IsUtf8(HtmlEntity.DeEntitize(charset.Value)))
                {
                    edits.Add(TextEdit.ForAttribute(text, charset, "utf-8"));
                }
                else if (
                    content != null &&
                    meta.GetAttributeValue("http-equiv", "").Trim().Equals("content-type", StringComparison.OrdinalIgnoreCase)
                )
                {
                    string value = HtmlEntity.DeEntitize(content.Value);
                    string updated = ContentCharset.Replace(value, "charset=utf-8");

                    if (updated != value)
                        edits.Add(TextEdit.ForAttribute(text, content, updated));
                }
            }

            return TextEdit.Apply(text, edits.Where(edit => edit != null));
        }

        /// <summary>
        /// Changes a stylesheet's @charset rule, if it has one, to
        /// UTF-8.
        /// </summary>
        /// <param name="css">The stylesheet.</param>
        /// <returns>The stylesheet with its @charset updated.</returns>
        private static string RewriteCssCharset(string css)
        {
            Match rule = CssCharsetRule.Match(css);

            if (!rule.Success || IsUtf8(rule.Groups["charset"].Value))
                return css;

            return "@charset \"utf-8\";" + css[rule.Length..];
        }

        /// <summary>
        /// Determines whether a charset label means UTF-8.
        /// </summary>
        /// <param name="label">A charset label.</param>
        /// <returns>True for utf-8 and utf8, in any case.</returns>
        private static bool IsUtf8(string label)
        {
            string trimmed = label.Trim();
            return trimmed.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("utf8", StringComparison.OrdinalIgnoreCase);
        }
    }
}
