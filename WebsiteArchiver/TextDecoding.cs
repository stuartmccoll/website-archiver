using System;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace WebsiteArchiver
{
    /// <summary>
    /// Decodes the HTML, CSS and web manifests a crawl captures, and labels
    /// the UTF-8 copies that links are rewritten in.
    /// </summary>
    public static class TextDecoding
    {
        // Browsers read these labels as windows-1252, which is a superset
        private static readonly string[] Windows1252Labels = { "iso-8859-1", "latin1", "l1", "us-ascii", "ascii" };

        private static readonly Regex HtmlMetaCharset = new(
            @"<meta\b[^>]*?\bcharset\s*=\s*[""']?\s*(?<charset>[\w.:\-]+)",
            RegexOptions.IgnoreCase
        );

        // As the CSS specification requires, only exactly this form, at the
        // very start of the file, declares an encoding
        private static readonly Regex CssCharset = new(@"^@charset ""(?<charset>[^""]*)"";");

        static TextDecoding()
        {
            // .NET only has UTF and Latin-1 encodings without this
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>
        /// Decodes a text file. The encoding is taken from, in order: a
        /// byte order mark; the charset in its Content-Type; for HTML, a
        /// meta charset declaration in its first 1024 bytes; for CSS, an
        /// @charset rule. Otherwise it's read as UTF-8.
        /// </summary>
        /// <param name="content">The file's bytes.</param>
        /// <param name="contentType">The file's Content-Type.</param>
        /// <param name="format">What kind of file it is.</param>
        /// <returns>The decoded text.</returns>
        public static string Decode(byte[] content, string contentType, CapturedFormat format)
        {
            Encoding encoding = FromLabel(GetCharset(contentType));

            if (encoding == null && format is CapturedFormat.Html or CapturedFormat.Css)
            {
                string start = Encoding.Latin1.GetString(content, 0, Math.Min(content.Length, 1024));
                Match declaration = format == CapturedFormat.Html ? HtmlMetaCharset.Match(start) : CssCharset.Match(start);

                // An ASCII-compatible declaration can't really mean UTF-16,
                // so browsers read it as UTF-8
                if (declaration.Success && FromLabel(declaration.Groups["charset"].Value) is Encoding declared)
                    encoding = declared is UnicodeEncoding or UTF32Encoding ? Encoding.UTF8 : declared;
            }

            using StreamReader reader = new(
                new MemoryStream(content),
                encoding ?? Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true
            );

            return reader.ReadToEnd();
        }

        /// <summary>
        /// Sets the charset in a Content-Type to UTF-8, adding it if there
        /// isn't one.
        /// </summary>
        /// <param name="contentType">A Content-Type header value.</param>
        /// <returns>The Content-Type with charset=utf-8.</returns>
        public static string WithUtf8Charset(string contentType)
        {
            if (MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue parsed))
            {
                parsed.CharSet = "utf-8";
                return parsed.ToString();
            }

            return Snapshot.MediaType(contentType) + "; charset=utf-8";
        }

        /// <summary>
        /// Gets the charset parameter from a Content-Type.
        /// </summary>
        /// <param name="contentType">A Content-Type header value, or
        /// null.</param>
        /// <returns>The charset, or null if there isn't one.</returns>
        private static string GetCharset(string contentType)
        {
            return (contentType ?? "")
                .Split(';')
                .Skip(1)
                .Select(parameter => parameter.Split('=', 2))
                .Where(pair => pair.Length == 2 && pair[0].Trim().Equals("charset", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair[1].Trim().Trim('"'))
                .FirstOrDefault();
        }

        /// <summary>
        /// Gets the encoding a charset label names.
        /// </summary>
        /// <param name="label">A charset label, e.g. utf-8, or
        /// null.</param>
        /// <returns>The encoding, or null if the label is missing or
        /// unknown.</returns>
        private static Encoding FromLabel(string label)
        {
            if (String.IsNullOrWhiteSpace(label))
                return null;

            label = label.Trim();

            if (Windows1252Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                label = "windows-1252";

            try
            {
                return Encoding.GetEncoding(label);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
