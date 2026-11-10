using System;
using System.Threading.Tasks;

namespace WebsiteArchiver
{
    class Program
    {
        /// <summary>
        /// Archives the site, exiting with a non-zero code if any file
        /// couldn't be fetched, so a partial archive isn't mistaken for a
        /// complete one. A partial snapshot is still published, marked
        /// incomplete.
        /// </summary>
        /// <returns>0 if everything was archived, otherwise 1.</returns>
        static async Task<int> Main(string[] args)
        {
            Archiver archiver;

            // Exceptions must be caught rather than left unhandled. An
            // unhandled exception ends the process with SIGABRT, which
            // Linux ignores for PID 1, so in a container started without
            // --init the process would hang instead of exiting.
            try
            {
                archiver = new();
                await archiver.Crawl();
            }
            catch (InvalidOperationException exception)
            {
                // Expected failures, such as DOMAIN being missing or the
                // homepage being unavailable, which need no stack trace
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Archiving failed: {exception}");
                return 1;
            }

            if (archiver.FailedAddresses.Count == 0)
                return 0;

            Console.Error.WriteLine(
                $"Snapshot {archiver.SnapshotId} was published, marked incomplete, because " +
                $"{archiver.FailedAddresses.Count} address(es) couldn't be fetched:"
            );

            foreach (String address in archiver.FailedAddresses)
                Console.Error.WriteLine($"\t{address}");

            return 1;
        }
    }
}
