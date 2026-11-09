using System;
using System.Threading.Tasks;

namespace WebsiteArchiver
{
    class Program
    {
        /// <summary>
        /// Archives the site, exiting with a non-zero code if any page or
        /// stylesheet couldn't be fetched, so a partial archive isn't
        /// mistaken for a complete one.
        /// </summary>
        /// <returns>0 if everything was archived, otherwise 1.</returns>
        static async Task<int> Main(string[] args)
        {
            Archiver archiver = new();
            await archiver.Crawl();

            if (archiver.FailedAddresses.Count == 0)
                return 0;

            Console.Error.WriteLine(
                $"Finished with {archiver.FailedAddresses.Count} address(es) that couldn't be fetched:"
            );

            foreach (String address in archiver.FailedAddresses)
                Console.Error.WriteLine($"\t{address}");

            return 1;
        }
    }
}
