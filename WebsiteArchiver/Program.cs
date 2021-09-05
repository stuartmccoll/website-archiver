using HtmlAgilityPack;
using System;
using System.Threading.Tasks;

namespace WebsiteArchiver
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Archiver archiver = new();

            await archiver.Crawl();
        }
    }
}
