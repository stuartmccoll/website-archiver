using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace WebsiteArchiver
{
    /// <summary>
    /// How a referenced address is used, which decides whether it's
    /// followed and how its response is read if its Content-Type doesn't
    /// say.
    /// </summary>
    public enum ReferenceKind
    {
        /// <summary>A link to another page, e.g. an a href. Only followed
        /// if it's beneath the site root.</summary>
        Page,

        /// <summary>A file the page needs, such as an image or script.
        /// Followed anywhere on the site's host.</summary>
        Resource,

        /// <summary>A stylesheet, parsed as CSS even if it isn't served
        /// as text/css.</summary>
        Stylesheet,

        /// <summary>A web app manifest, parsed for its icons even if it
        /// isn't served as application/manifest+json.</summary>
        WebManifest,

        /// <summary>A link to another document that isn't crawled, such
        /// as link rel="canonical" or rel="alternate". Its address is
        /// rewritten if it's in the snapshot, but it's never
        /// fetched.</summary>
        Link,
    }

    /// <summary>
    /// An address found in a captured file.
    /// </summary>
    /// <param name="Address">The absolute address, without its query string
    /// or fragment.</param>
    /// <param name="Kind">How the address is used.</param>
    public record Reference(Uri Address, ReferenceKind Kind);

    /// <summary>
    /// Decides what a reference found in a file should be replaced with.
    /// </summary>
    /// <param name="value">The reference as written, decoded from HTML if
    /// it was in an attribute, e.g. ../css/site.css#x.</param>
    /// <param name="baseAddress">The address it resolves against.</param>
    /// <param name="kind">How the address is used.</param>
    /// <returns>Its replacement, or <paramref name="value"/> to leave it
    /// alone.</returns>
    public delegate string ReferenceMapper(string value, Uri baseAddress, ReferenceKind kind);

    /// <summary>
    /// A replacement of part of a text.
    /// </summary>
    /// <param name="Start">Where the replaced text starts.</param>
    /// <param name="Length">How long the replaced text is.</param>
    /// <param name="Replacement">The text that replaces it.</param>
    public record TextEdit(int Start, int Length, string Replacement)
    {
        /// <summary>
        /// Applies edits to a text. An edit that overlaps an earlier one
        /// is ignored.
        /// </summary>
        /// <param name="text">The text to edit.</param>
        /// <param name="edits">The edits, in any order, with positions in
        /// the unedited text.</param>
        /// <returns>The edited text.</returns>
        public static string Apply(string text, IEnumerable<TextEdit> edits)
        {
            StringBuilder result = new(text.Length);
            int position = 0;

            foreach (TextEdit edit in edits.OrderBy(edit => edit.Start))
            {
                if (edit.Start < position)
                    continue;

                result.Append(text, position, edit.Start - position);
                result.Append(edit.Replacement);
                position = edit.Start + edit.Length;
            }

            result.Append(text, position, text.Length - position);
            return result.ToString();
        }

        /// <summary>
        /// Creates an edit that gives an HTML attribute a new value. The
        /// value is HTML-encoded, and quoted if it wasn't before.
        /// </summary>
        /// <param name="source">The HTML the attribute was parsed
        /// from.</param>
        /// <param name="attribute">The attribute.</param>
        /// <param name="value">Its new value, not HTML-encoded.</param>
        /// <returns>The edit, or null if the attribute's position in the
        /// source isn't known, as for one the parser added
        /// itself.</returns>
        public static TextEdit ForAttribute(string source, HtmlAttribute attribute, string value)
        {
            int start = attribute.ValueStartIndex;
            int length = attribute.ValueLength;

            if (
                attribute.QuoteType == AttributeValueQuote.WithoutValue ||
                start < 0 ||
                start + length > source.Length ||
                String.CompareOrdinal(source, start, attribute.Value, 0, length) != 0 ||
                (attribute.Value ?? "").Length != length
            )
            {
                return null;
            }

            string encoded = value.Replace("&", "&amp;");

            encoded = attribute.QuoteType switch
            {
                AttributeValueQuote.SingleQuote => encoded.Replace("'", "&#39;"),
                AttributeValueQuote.DoubleQuote => encoded.Replace("\"", "&quot;"),
                _ => "\"" + encoded.Replace("\"", "&quot;") + "\"",
            };

            return new TextEdit(start, length, encoded);
        }
    }

    /// <summary>
    /// Finds the addresses that HTML pages, stylesheets and web app
    /// manifests refer to. Each kind of file has a Map method, which finds
    /// the references and lets a <see cref="ReferenceMapper"/> replace
    /// them, so that finding and rewriting references always agree on
    /// where they are.
    /// </summary>
    public static class References
    {
        private static readonly Regex CssComment = new(@"/\*.*?\*/", RegexOptions.Singleline);

        private static readonly Regex CssUrl = new(
            @"url\(\s*(?:""(?<url>[^""]*)""|'(?<url>[^']*)'|(?<url>[^)\s]*))\s*\)",
            RegexOptions.IgnoreCase
        );

        private static readonly Regex CssImport = new(
            @"@import\s+(?:""(?<url>[^""]*)""|'(?<url>[^']*)')",
            RegexOptions.IgnoreCase
        );

        private static readonly JsonSerializerOptions JsonStringOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // rel values for links to files a page needs. Others, such as
        // canonical, alternate and next, are links to other documents.
        private static readonly HashSet<string> ResourceRels = new(StringComparer.OrdinalIgnoreCase)
        {
            "stylesheet",
            "icon",
            "apple-touch-icon",
            "apple-touch-icon-precomposed",
            "mask-icon",
            "manifest",
            "preload",
            "modulepreload",
        };

        /// <summary>
        /// Finds the pages an HTML page links to and the files it uses. See
        /// <see cref="MapHtml"/> for which ones.
        /// </summary>
        /// <param name="html">The parsed page.</param>
        /// <param name="pageAddress">The address the page was served
        /// from.</param>
        /// <returns>Every reference found, in document order. The same
        /// address can appear more than once.</returns>
        public static List<Reference> FromHtml(HtmlDocument html, Uri pageAddress)
        {
            List<Reference> references = new();
            MapHtml(html, pageAddress, Collect(references));
            return references;
        }

        /// <summary>
        /// Finds the files a stylesheet uses. See <see cref="MapCss"/> for
        /// which ones.
        /// </summary>
        /// <param name="css">The stylesheet, or the contents of a style
        /// attribute or element.</param>
        /// <param name="baseAddress">The address relative references
        /// resolve against: the stylesheet's own address, or the page's
        /// for inline CSS.</param>
        /// <returns>Every reference found: imports first, then
        /// url(…).</returns>
        public static List<Reference> FromCss(string css, Uri baseAddress)
        {
            List<Reference> references = new();
            MapCss(css, baseAddress, Collect(references));
            return references;
        }

        /// <summary>
        /// Finds the icons a web app manifest lists in icons[].src.
        /// </summary>
        /// <param name="json">The manifest's contents.</param>
        /// <param name="manifestAddress">The manifest's address, which
        /// relative icon addresses resolve against.</param>
        /// <returns>The icons' addresses, or none if the manifest isn't
        /// valid JSON.</returns>
        public static List<Reference> FromWebManifest(string json, Uri manifestAddress)
        {
            List<Reference> references = new();

            if (MapWebManifest(json, manifestAddress, Collect(references)) == null)
            {
                Console.WriteLine($"Couldn't read the web manifest at {manifestAddress}, because it isn't valid JSON");
                return new();
            }

            return references;
        }

        /// <summary>
        /// Finds the references in an HTML page and works out how to
        /// replace them:
        /// <list type="bullet">
        /// <item><description>Pages: href on a and area.</description></item>
        /// <item><description>Other documents, which aren't followed:
        /// link href with any other rel, such as canonical or
        /// alternate.</description></item>
        /// <item><description>Files: link href for stylesheets, icons, the
        /// web manifest and preloads; src on img, source, script, video,
        /// audio, track, embed, iframe and input; srcset and imagesrcset;
        /// poster; object data; and url(…) or @import in style attributes
        /// and style elements.</description></item>
        /// </list>
        /// Relative addresses resolve against the page's address, or its
        /// base href if it has one. Links that are only a fragment, such as
        /// #top, are left out.
        /// </summary>
        /// <param name="html">The parsed page.</param>
        /// <param name="pageAddress">The address the page was served
        /// from.</param>
        /// <param name="map">Decides what each reference is replaced
        /// with.</param>
        /// <returns>The edits to the page's source that make those
        /// replacements.</returns>
        public static List<TextEdit> MapHtml(HtmlDocument html, Uri pageAddress, ReferenceMapper map)
        {
            List<TextEdit> edits = new();
            string source = html.Text;
            Uri baseAddress = GetBaseAddress(html, pageAddress);

            string MapAddress(string value, ReferenceKind kind)
            {
                string trimmed = value.Trim();

                if (trimmed.StartsWith('#'))
                    return value;

                string mapped = map(trimmed, baseAddress, kind);
                return mapped == trimmed ? value : mapped;
            }

            void MapAttribute(HtmlNode node, string name, Func<string, string> mapValue)
            {
                HtmlAttribute attribute = node.Attributes[name];

                if (attribute == null)
                    return;

                string value = HtmlEntity.DeEntitize(attribute.Value ?? "");
                string mapped = mapValue(value);

                if (mapped != value && TextEdit.ForAttribute(source, attribute, mapped) is TextEdit edit)
                    edits.Add(edit);
            }

            void Address(HtmlNode node, string name, ReferenceKind kind) =>
                MapAttribute(node, name, value => MapAddress(value, kind));

            void Srcset(HtmlNode node, string name) =>
                MapAttribute(node, name, value => MapSrcset(value, candidate => MapAddress(candidate, ReferenceKind.Resource)));

            foreach (HtmlNode node in html.DocumentNode.Descendants())
            {
                if (node.NodeType != HtmlNodeType.Element)
                    continue;

                switch (node.Name)
                {
                    case "a":
                    case "area":
                        Address(node, "href", ReferenceKind.Page);
                        break;

                    case "link":
                        ReferenceKind linkKind = GetLinkKind(node);
                        Address(node, "href", linkKind);

                        if (linkKind != ReferenceKind.Link)
                            Srcset(node, "imagesrcset");
                        break;

                    case "img":
                    case "source":
                        Address(node, "src", ReferenceKind.Resource);
                        Srcset(node, "srcset");
                        break;

                    case "video":
                        Address(node, "src", ReferenceKind.Resource);
                        Address(node, "poster", ReferenceKind.Resource);
                        break;

                    case "script":
                    case "audio":
                    case "track":
                    case "embed":
                    case "iframe":
                    case "input":
                        Address(node, "src", ReferenceKind.Resource);
                        break;

                    case "object":
                        Address(node, "data", ReferenceKind.Resource);
                        break;

                    case "style":
                        // A style element's contents are raw text, so
                        // entities in it aren't decoded
                        string css = node.InnerHtml;
                        string mappedCss = MapCss(css, baseAddress, map);

                        if (
                            mappedCss != css &&
                            node.InnerStartIndex >= 0 &&
                            node.InnerStartIndex + node.InnerLength <= source.Length &&
                            String.CompareOrdinal(source, node.InnerStartIndex, css, 0, node.InnerLength) == 0 &&
                            css.Length == node.InnerLength
                        )
                        {
                            edits.Add(new TextEdit(node.InnerStartIndex, node.InnerLength, mappedCss));
                        }
                        break;
                }

                MapAttribute(node, "style", css => MapCss(css, baseAddress, map));
            }

            return edits;
        }

        /// <summary>
        /// Finds the references in a stylesheet, through url(…) and
        /// @import, and replaces them. Comments are ignored, as are empty
        /// references and those to a fragment, such as url(#gradient).
        /// </summary>
        /// <param name="css">The stylesheet, or the contents of a style
        /// attribute or element.</param>
        /// <param name="baseAddress">The address relative references
        /// resolve against.</param>
        /// <param name="map">Decides what each reference is replaced
        /// with.</param>
        /// <returns>The stylesheet with the replacements made.</returns>
        public static string MapCss(string css, Uri baseAddress, ReferenceMapper map)
        {
            List<TextEdit> edits = new();

            // Blank out comments rather than removing them, so positions
            // still match the original
            string searchable = CssComment.Replace(css, comment => new string(' ', comment.Length));

            IEnumerable<(Match Match, ReferenceKind Kind)> matches = CssImport.Matches(searchable)
                .Select(match => (match, ReferenceKind.Stylesheet))
                .Concat(CssUrl.Matches(searchable).Select(match => (match, ReferenceKind.Resource)));

            foreach ((Match match, ReferenceKind kind) in matches)
            {
                Group url = match.Groups["url"];
                string value = url.Value.Trim();

                if (value.Length == 0 || value.StartsWith('#'))
                    continue;

                string mapped = map(value, baseAddress, kind);

                if (mapped != value)
                {
                    char quote = url.Index > 0 ? css[url.Index - 1] : ' ';
                    edits.Add(new TextEdit(url.Index, url.Length, EscapeCssUrl(mapped, quote)));
                }
            }

            return TextEdit.Apply(css, edits);
        }

        /// <summary>
        /// Finds the icons a web app manifest lists in icons[].src and
        /// replaces them.
        /// </summary>
        /// <param name="json">The manifest's contents.</param>
        /// <param name="manifestAddress">The manifest's address, which
        /// relative icon addresses resolve against.</param>
        /// <param name="map">Decides what each icon's address is replaced
        /// with.</param>
        /// <returns>The manifest with the replacements made, or null if it
        /// isn't valid JSON.</returns>
        public static string MapWebManifest(string json, Uri manifestAddress, ReferenceMapper map)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(json);
            List<(long Start, int Length, string Replacement)> edits = new();

            // The containers the reader is inside, and the property each
            // one is the value of
            List<(JsonTokenType Type, string Property)> containers = new();
            string property = null;

            try
            {
                Utf8JsonReader reader = new(utf8);

                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray:
                            containers.Add((reader.TokenType, property));
                            property = null;
                            break;

                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray:
                            containers.RemoveAt(containers.Count - 1);
                            property = null;
                            break;

                        case JsonTokenType.PropertyName:
                            property = reader.GetString();
                            break;

                        case JsonTokenType.String:
                            bool isIconSrc =
                                property == "src" &&
                                containers.Count == 3 &&
                                containers[0].Type == JsonTokenType.StartObject &&
                                containers[1] == (JsonTokenType.StartArray, "icons") &&
                                containers[2].Type == JsonTokenType.StartObject;

                            if (isIconSrc)
                            {
                                string value = reader.GetString();
                                string mapped = map(value, manifestAddress, ReferenceKind.Resource);

                                // The token includes its quotes, which
                                // ValueSpan doesn't
                                if (mapped != value)
                                    edits.Add((reader.TokenStartIndex, reader.ValueSpan.Length + 2, JsonSerializer.Serialize(mapped, JsonStringOptions)));
                            }

                            property = null;
                            break;

                        default:
                            property = null;
                            break;
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            using MemoryStream result = new();
            int position = 0;

            foreach ((long start, int length, string replacement) in edits)
            {
                result.Write(utf8, position, (int)start - position);
                result.Write(Encoding.UTF8.GetBytes(replacement));
                position = (int)start + length;
            }

            result.Write(utf8, position, utf8.Length - position);
            return Encoding.UTF8.GetString(result.ToArray());
        }

        /// <summary>
        /// Resolves a reference, which may be absolute or relative, and
        /// removes any query string or fragment, so each file is fetched
        /// and stored once under a name derived from its path.
        /// </summary>
        /// <param name="baseAddress">The address relative references
        /// resolve against.</param>
        /// <param name="reference">The reference, already decoded from
        /// HTML.</param>
        /// <returns>The absolute address, or null if it can't be parsed or
        /// isn't http or https.</returns>
        public static Uri Resolve(Uri baseAddress, string reference)
        {
            if (!Uri.TryCreate(baseAddress, reference.Trim(), out Uri address))
                return null;

            if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
                return null;

            return new Uri(address.GetLeftPart(UriPartial.Path));
        }

        /// <summary>
        /// Creates a mapper that records every reference it's given, and
        /// leaves each one as it is.
        /// </summary>
        /// <param name="references">Where to record the
        /// references.</param>
        /// <returns>The mapper.</returns>
        private static ReferenceMapper Collect(List<Reference> references)
        {
            return (value, baseAddress, kind) =>
            {
                Uri address = kind == ReferenceKind.Link ? null : Resolve(baseAddress, value);

                if (address != null)
                    references.Add(new Reference(address, kind));

                return value;
            };
        }

        /// <summary>
        /// Works out whether a link element's href is a file the page
        /// needs, from its rel, and if so what kind.
        /// </summary>
        /// <param name="node">A link element.</param>
        /// <returns>The kind of reference, which is
        /// <see cref="ReferenceKind.Link"/> if the link is to another
        /// document, such as rel="canonical".</returns>
        private static ReferenceKind GetLinkKind(HtmlNode node)
        {
            string[] rels = node.GetAttributeValue("rel", "")
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            if (!rels.Any(ResourceRels.Contains))
                return ReferenceKind.Link;

            if (rels.Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
                return ReferenceKind.Stylesheet;

            if (rels.Contains("manifest", StringComparer.OrdinalIgnoreCase))
                return ReferenceKind.WebManifest;

            return ReferenceKind.Resource;
        }

        /// <summary>
        /// Gets the address relative links in a page resolve against: its
        /// base href if it has a usable one, otherwise its own address.
        /// </summary>
        /// <param name="html">The parsed page.</param>
        /// <param name="pageAddress">The address the page was served
        /// from.</param>
        /// <returns>The base address.</returns>
        private static Uri GetBaseAddress(HtmlDocument html, Uri pageAddress)
        {
            HtmlNode baseElement = GetBaseElement(html);

            if (baseElement == null)
                return pageAddress;

            string href = HtmlEntity.DeEntitize(baseElement.Attributes["href"].Value).Trim();

            if (
                Uri.TryCreate(pageAddress, href, out Uri baseAddress) &&
                (baseAddress.Scheme == Uri.UriSchemeHttp || baseAddress.Scheme == Uri.UriSchemeHttps)
            )
            {
                return baseAddress;
            }

            return pageAddress;
        }

        /// <summary>
        /// Gets the base element that sets a page's base address: the
        /// first one with an href.
        /// </summary>
        /// <param name="html">The parsed page.</param>
        /// <returns>The base element, or null if there isn't one.</returns>
        public static HtmlNode GetBaseElement(HtmlDocument html)
        {
            return html.DocumentNode
                .Descendants("base")
                .FirstOrDefault(node => node.Attributes.Contains("href"));
        }

        /// <summary>
        /// Replaces the addresses in a srcset attribute, such as
        /// "small.png 1x, large.png 2x", following the HTML specification's
        /// parsing rules so that commas inside addresses (as in data:
        /// addresses) don't split them.
        /// </summary>
        /// <param name="srcset">The attribute's value.</param>
        /// <param name="map">Gives each address's replacement.</param>
        /// <returns>The attribute's new value.</returns>
        private static string MapSrcset(string srcset, Func<string, string> map)
        {
            List<TextEdit> edits = new();
            int position = 0;

            while (position < srcset.Length)
            {
                while (position < srcset.Length && (Char.IsWhiteSpace(srcset[position]) || srcset[position] == ','))
                    position++;

                int start = position;

                while (position < srcset.Length && !Char.IsWhiteSpace(srcset[position]))
                    position++;

                int length = position - start;

                if (srcset[start..position].EndsWith(','))
                {
                    while (length > 0 && srcset[start + length - 1] == ',')
                        length--;
                }
                else
                {
                    // Skip descriptors such as 2x or 480w, up to the next
                    // comma that isn't inside parentheses
                    bool inParentheses = false;

                    while (position < srcset.Length && (inParentheses || srcset[position] != ','))
                    {
                        if (srcset[position] == '(')
                            inParentheses = true;
                        else if (srcset[position] == ')')
                            inParentheses = false;

                        position++;
                    }
                }

                if (length > 0)
                {
                    string address = srcset.Substring(start, length);
                    string mapped = map(address);

                    if (mapped != address)
                        edits.Add(new TextEdit(start, length, mapped));
                }
            }

            return TextEdit.Apply(srcset, edits);
        }

        /// <summary>
        /// Percent-encodes the characters that would end a url(…) value
        /// early, so a replacement address can go where the original was.
        /// </summary>
        /// <param name="address">The replacement address.</param>
        /// <param name="quote">The character before the original address:
        /// the quote it's inside, or anything else if it isn't
        /// quoted.</param>
        /// <returns>The address, safe to insert.</returns>
        private static string EscapeCssUrl(string address, char quote)
        {
            StringBuilder escaped = new(address.Length);

            foreach (char character in address)
            {
                bool unsafeCharacter = quote switch
                {
                    '"' or '\'' => character == quote || character == '\\' || character == '\n',
                    _ => character is '(' or ')' or '"' or '\'' or '\\' || Char.IsWhiteSpace(character),
                };

                if (unsafeCharacter)
                    escaped.Append($"%{(int)character:X2}");
                else
                    escaped.Append(character);
            }

            return escaped.ToString();
        }
    }
}
