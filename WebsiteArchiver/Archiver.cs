using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebsiteArchiver
{
    public class Archiver
    {
        // TODO: Consider CSS, XML, etc.

        private AzureBlobStorageClient blobStorageClient;
        private HtmlWeb web;
        private List<String> visitedLinks;

        public Archiver()
        {
            this.blobStorageClient = new();
            this.web = new HtmlWeb();
            this.visitedLinks = new List<String>();
        }

        async public Task Crawl()
        {
            HtmlDocument htmlDoc = web.Load("http://www.stuartmccoll.co.uk");

            await this.StoreDocument(htmlDoc, "main");

            await this.CrawlLinks(htmlDoc);
        }

        private HtmlNodeCollection GetAllLinks(HtmlDocument htmlDoc)
        {
            HtmlNodeCollection Links = htmlDoc.DocumentNode.SelectNodes("//a[@href]");

            return this.RemoveUnnecessaryLinks(Links);
        }

        private async Task CrawlLinks(HtmlDocument htmlDoc)
        {
            HtmlNodeCollection links = this.GetAllLinks(htmlDoc);

            foreach (HtmlNode link in links)
            {
                await this.CrawlLink(link);
            }
        }

        private HtmlNodeCollection RemoveUnnecessaryLinks(HtmlNodeCollection links)
        {
            List<HtmlNode> linksToRemove = new List<HtmlNode>();

            foreach (HtmlNode link in links)
            {
                if (
                    !link.Attributes["href"].Value.StartsWith("https://stuartmccoll.github.io") ||
                    link.Attributes["href"].Value.EndsWith(".xml")
                    )
                {
                    linksToRemove.Add(link);
                }
            }

            foreach (HtmlNode link in linksToRemove)
            {
                links.Remove(link);
            }

            return links;
        }

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

        private async Task StoreDocument(HtmlDocument htmlDoc, String address)
        {
            address = address.Replace("https://stuartmccoll.github.io/", "");

            String[] splitAddress = address.Split("/");
            splitAddress = splitAddress.Where((value) => value != "").ToArray();

            Console.WriteLine($"Storing {splitAddress[splitAddress.Length - 1]}");

            string fileName = splitAddress[splitAddress.Length - 1];

            await this.blobStorageClient.UploadBlob(fileName, htmlDoc.Text);
        }
    }
}
