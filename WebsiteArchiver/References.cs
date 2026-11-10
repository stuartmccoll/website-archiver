using System;
using System.Collections.Generic;
using System.Linq;
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
    }

    /// <summary>
    /// An address found in a captured file.
    /// </summary>
    /// <param name="Address">The absolute address, without its query string
    /// or fragment.</param>
    /// <param name="Kind">How the address is used.</param>
    public record Reference(Uri Address, ReferenceKind Kind);

    /// <summary>
    /// Finds the addresses that HTML pages, stylesheets and web app
    /// manifests refer to.
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
        /// Finds the pages an HTML page links to and the files it uses:
        /// <list type="bullet">
        /// <item><description>Pages: href on a and area.</description></item>
        /// <item><description>Files: link href for stylesheets, icons, the
        /// web manifest and preloads; src on img, source, script, video,
        /// audio, track, embed, iframe and input; srcset and imagesrcset;
        /// poster; object data; and url(…) or @import in style attributes
        /// and style elements.</description></item>
        /// </list>
        /// Relative addresses resolve against the page's address, or its
        /// base href if it has one. Links that are only a fragment, such as
        /// #top, and anything not http or https, such as mailto: or data:,
        /// are left out.
        /// </summary>
        /// <param name="html">The parsed page.</param>
        /// <param name="pageAddress">The address the page was served
        /// from.</param>
        /// <returns>Every reference found, in document order. The same
        /// address can appear more than once.</returns>
        public static List<Reference> FromHtml(HtmlDocument html, Uri pageAddress)
        {
            List<Reference> references = new();
            Uri baseAddress = GetBaseAddress(html, pageAddress);

            void Add(string value, ReferenceKind kind)
            {
                string decoded = HtmlEntity.DeEntitize(value ?? "").Trim();

                if (decoded.StartsWith('#'))
                    return;

                Uri address = Resolve(baseAddress, decoded);

                if (address != null)
                    references.Add(new Reference(address, kind));
            }

            void AddSrcset(string value)
            {
                foreach (string candidate in ParseSrcset(HtmlEntity.DeEntitize(value ?? "")))
                    Add(candidate, ReferenceKind.Resource);
            }

            void AddCss(string css)
            {
                references.AddRange(FromCss(HtmlEntity.DeEntitize(css ?? ""), baseAddress));
            }

            foreach (HtmlNode node in html.DocumentNode.Descendants())
            {
                if (node.NodeType != HtmlNodeType.Element)
                    continue;

                switch (node.Name)
                {
                    case "a":
                    case "area":
                        if (node.Attributes.Contains("href"))
                            Add(node.Attributes["href"].Value, ReferenceKind.Page);
                        break;

                    case "link":
                        AddLink(node, Add, AddSrcset);
                        break;

                    case "img":
                    case "source":
                        AddAttribute(node, "src", Add);
                        if (node.Attributes.Contains("srcset"))
                            AddSrcset(node.Attributes["srcset"].Value);
                        break;

                    case "video":
                        AddAttribute(node, "src", Add);
                        AddAttribute(node, "poster", Add);
                        break;

                    case "script":
                    case "audio":
                    case "track":
                    case "embed":
                    case "iframe":
                    case "input":
                        AddAttribute(node, "src", Add);
                        break;

                    case "object":
                        AddAttribute(node, "data", Add);
                        break;

                    case "style":
                        AddCss(node.InnerText);
                        break;
                }

                if (node.Attributes.Contains("style"))
                    AddCss(node.Attributes["style"].Value);
            }

            return references;
        }

        /// <summary>
        /// Finds the files a stylesheet uses, through url(…) and @import.
        /// Comments are ignored, as are data: addresses and references to
        /// a fragment such as url(#gradient).
        /// </summary>
        /// <param name="css">The stylesheet, or the contents of a style
        /// attribute or element.</param>
        /// <param name="baseAddress">The address relative references
        /// resolve against: the stylesheet's own address, or the page's
        /// for inline CSS.</param>
        /// <returns>Every reference found, in order.</returns>
        public static List<Reference> FromCss(string css, Uri baseAddress)
        {
            List<Reference> references = new();
            string withoutComments = CssComment.Replace(css, "");

            void Add(Match match, ReferenceKind kind)
            {
                string value = match.Groups["url"].Value.Trim();

                if (value.Length == 0 || value.StartsWith('#'))
                    return;

                Uri address = Resolve(baseAddress, value);

                if (address != null)
                    references.Add(new Reference(address, kind));
            }

            foreach (Match match in CssImport.Matches(withoutComments))
                Add(match, ReferenceKind.Stylesheet);

            foreach (Match match in CssUrl.Matches(withoutComments))
                Add(match, ReferenceKind.Resource);

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
            JsonDocument manifest;

            try
            {
                manifest = JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                Console.WriteLine($"Couldn't read the web manifest at {manifestAddress}: {exception.Message}");
                return references;
            }

            using (manifest)
            {
                if (
                    manifest.RootElement.ValueKind != JsonValueKind.Object ||
                    !manifest.RootElement.TryGetProperty("icons", out JsonElement icons) ||
                    icons.ValueKind != JsonValueKind.Array
                )
                {
                    return references;
                }

                foreach (JsonElement icon in icons.EnumerateArray())
                {
                    if (
                        icon.ValueKind == JsonValueKind.Object &&
                        icon.TryGetProperty("src", out JsonElement src) &&
                        src.ValueKind == JsonValueKind.String
                    )
                    {
                        Uri address = Resolve(manifestAddress, src.GetString());

                        if (address != null)
                            references.Add(new Reference(address, ReferenceKind.Resource));
                    }
                }
            }

            return references;
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
        /// Adds a link element's href if its rel says it's a file the page
        /// needs, along with any imagesrcset.
        /// </summary>
        /// <param name="node">A link element.</param>
        /// <param name="add">Adds an address of the given kind.</param>
        /// <param name="addSrcset">Adds every address in a srcset.</param>
        private static void AddLink(
            HtmlNode node,
            Action<string, ReferenceKind> add,
            Action<string> addSrcset
        )
        {
            string[] rels = node.GetAttributeValue("rel", "")
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            if (!rels.Any(ResourceRels.Contains))
                return;

            ReferenceKind kind = ReferenceKind.Resource;

            if (rels.Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
                kind = ReferenceKind.Stylesheet;
            else if (rels.Contains("manifest", StringComparer.OrdinalIgnoreCase))
                kind = ReferenceKind.WebManifest;

            if (node.Attributes.Contains("href"))
                add(node.Attributes["href"].Value, kind);

            if (node.Attributes.Contains("imagesrcset"))
                addSrcset(node.Attributes["imagesrcset"].Value);
        }

        /// <summary>
        /// Adds the value of an attribute as a file the page needs, if the
        /// element has it.
        /// </summary>
        /// <param name="node">An element.</param>
        /// <param name="attribute">The attribute's name.</param>
        /// <param name="add">Adds an address of the given kind.</param>
        private static void AddAttribute(HtmlNode node, string attribute, Action<string, ReferenceKind> add)
        {
            if (node.Attributes.Contains(attribute))
                add(node.Attributes[attribute].Value, ReferenceKind.Resource);
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
            HtmlNode baseElement = html.DocumentNode
                .Descendants("base")
                .FirstOrDefault(node => node.Attributes.Contains("href"));

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
        /// Gets the addresses from a srcset attribute, such as
        /// "small.png 1x, large.png 2x", following the HTML specification's
        /// parsing rules so that commas inside addresses (as in data:
        /// addresses) don't split them.
        /// </summary>
        /// <param name="srcset">The attribute's value.</param>
        /// <returns>The addresses, without their descriptors.</returns>
        private static IEnumerable<string> ParseSrcset(string srcset)
        {
            int position = 0;

            while (position < srcset.Length)
            {
                while (position < srcset.Length && (Char.IsWhiteSpace(srcset[position]) || srcset[position] == ','))
                    position++;

                int start = position;

                while (position < srcset.Length && !Char.IsWhiteSpace(srcset[position]))
                    position++;

                string address = srcset.Substring(start, position - start);

                if (address.EndsWith(','))
                {
                    address = address.TrimEnd(',');
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

                if (address.Length > 0)
                    yield return address;
            }
        }
    }
}
