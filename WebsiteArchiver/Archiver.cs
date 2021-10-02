using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace WebsiteArchiver
{
    public class Archiver
    {

        private AzureBlobStorageClient blobStorageClient;
        private HtmlWeb web;
        private List<String> visitedLinks;

        public Archiver()
        {
            this.blobStorageClient = new();
            this.web = new HtmlWeb();
            this.visitedLinks = new List<String>();
        }

        /// <summary>
        /// Main method responsible for orchestrating crawling and storage
        /// of all applicable pages.
        /// </summary>
        /// <returns>An awaitable Task.</returns>
        async public Task Crawl()
        {
            HtmlDocument htmlDoc = web.Load(
                Environment.GetEnvironmentVariable("DOMAIN")
            );

            await this.CrawlCascadingStyleSheet(htmlDoc);

            await this.StoreDocument(htmlDoc, "main");

            await this.CrawlLinks(htmlDoc);
        }

        /// <summary>
        /// Crawls any cascading stylesheets  linked from the given HTML
        /// document and orchestrates their storage in Azure Blob Storage.
        /// </summary>
        /// <param name="htmlDoc">An HTML document.</param>
        /// <returns>An awaitable Task.</returns>
        async private Task CrawlCascadingStyleSheet(HtmlDocument htmlDoc)
        {
            HtmlNodeCollection stylesheets = htmlDoc.DocumentNode.SelectNodes(
                "//link[@rel='stylesheet'][@href]"
            );

            foreach (HtmlNode stylesheet in stylesheets)
            {
                string address = stylesheet.Attributes["href"].Value.Replace(
                    Environment.GetEnvironmentVariable("DOMAIN"),
                    ""
                );

                String[] splitAddress = address.Split("/");
                splitAddress = splitAddress.Where((value) => value != "").ToArray();

                Console.WriteLine($"Storing {splitAddress[splitAddress.Length - 1]}");

                string fileName = splitAddress[splitAddress.Length - 1];

                HttpClient client = new();

                using var stream = await client.GetStreamAsync(
                    $"{Environment.GetEnvironmentVariable("DOMAIN")}{stylesheet.Attributes["href"].Value}"
                );

                StreamReader reader = new StreamReader(stream);
                string fileContents = reader.ReadToEnd();

                await this.blobStorageClient.UploadBlob(fileName, "", fileContents);
            }
        }

        /// <summary>
        /// Parses the current document and builds a list of all links
        /// (anchor tags with an href attribute) within the page.
        /// </summary>
        /// <param name="htmlDoc">The current document</param>
        /// <returns>A collection of links.</returns>
        private HtmlNodeCollection GetAllLinks(HtmlDocument htmlDoc)
        {
            HtmlNodeCollection links = htmlDoc.DocumentNode.SelectNodes("//a[@href]");

            return this.RemoveUnnecessaryLinks(links);
        }

        /// <summary>
        /// Retrieves all links in a given HTML page and orchestrates
        /// crawling and storage of each.
        /// </summary>
        /// <param name="htmlDoc">An HTML document.</param>
        /// <returns>An awaitable Task.</returns>
        private async Task CrawlLinks(HtmlDocument htmlDoc)
        {
            HtmlNodeCollection links = this.GetAllLinks(htmlDoc);

            foreach (HtmlNode link in links)
            {
                await this.CrawlLink(link);
            }
        }

        /// <summary>
        /// Removes any unwanted links from the collection of links
        /// that will be crawled by the application. These consist of:
        /// <list type="bullet">
        /// <item>
        /// <description>Anything not on the current domain.</description>
        /// </item>
        /// <item>
        /// <description>Anything that can't be parsed as HTML, such as XML
        /// files.</description>
        /// </item>
        /// </list>
        /// </summary>
        /// <param name="links">Collection of links that the application
        /// will attempt to crawl.</param>
        /// <returns>An updated collection of links that the application
        /// will attempt to crawl, with any undesirable links
        /// removed.</returns>
        private HtmlNodeCollection RemoveUnnecessaryLinks(HtmlNodeCollection links)
        {
            List<HtmlNode> linksToRemove = new List<HtmlNode>();

            foreach (HtmlNode link in links)
            {
                if (
                    !link.Attributes["href"].Value.StartsWith(
                        Environment.GetEnvironmentVariable("DOMAIN")
                    ) ||
                    link.Attributes["href"].Value.EndsWith(".xml")
                )
                {
                    linksToRemove.Add(link);
                }
            }

            foreach (HtmlNode link in linksToRemove)
                links.Remove(link);

            return links;
        }

        /// <summary>
        /// Visits a given link if it hasn't seen it before, handing the
        /// document off for storage and continuing to crawl any further
        /// links found.
        /// </summary>
        /// <param name="link">An anchor tag with a populated href
        /// attribute.</param>
        /// <returns>An awaitable Task.</returns>
        private async Task CrawlLink(HtmlNode link)
        {
            String href = link.Attributes["href"].Value;

            if (this.visitedLinks.Contains(href))
            {
                Console.WriteLine($"Already visited {href}");
            }
            else
            {
                HtmlDocument htmlDoc = this.web.Load(href);

                this.visitedLinks.Add(href);

                await this.StoreDocument(htmlDoc, href);

                Console.WriteLine($"Crawling {href}");

                await this.CrawlLinks(htmlDoc);
            }
        }

        /// <summary>
        /// Orchestrates storage of a document in Azure Blob Storage.
        /// </summary>
        /// <param name="htmlDoc">An HTML document.</param>
        /// <param name="address">The address of the HTML document.</param>
        /// <returns>An awaitable Task.</returns>
        private async Task StoreDocument(HtmlDocument htmlDoc, String address)
        {
            string fileName = address.Replace(
                Environment.GetEnvironmentVariable("DOMAIN"),
                ""
            );

            if (fileName.EndsWith("/"))
                fileName = fileName.Remove(fileName.Length - 1, 1);

            string[] splitFileName = fileName.Split(".");

            // Assume .html by default
            string fileExtension = "html";

            // If split on a period, use last element as file extension
            if (splitFileName.Length > 1)
                fileExtension = fileName.Split(".")[splitFileName.Length - 1];

            await this.blobStorageClient.UploadBlob(fileName, fileExtension, htmlDoc.Text);
        }
    }
}
