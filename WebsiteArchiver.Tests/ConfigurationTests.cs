namespace WebsiteArchiver.Tests
{
    /// <summary>
    /// Tests how the storage configuration is read and checked. The real
    /// workload identity sign-in can't run here, because the SDK only
    /// sends tokens over https and it would contact Microsoft Entra ID, so
    /// these stop at constructing the <see cref="Archiver"/>, which makes
    /// no requests.
    /// </summary>
    public class ConfigurationTests
    {
        private const string Endpoint = "https://example.blob.core.windows.net/";

        private static readonly string[] Variables =
        {
            "DOMAIN",
            "AZURE_BLOB_STORAGE_CONNECTION_STRING",
            "AZURE_BLOB_STORAGE_ENDPOINT",
            "AZURE_BLOB_STORAGE_CONTAINER_NAME",
            "AZURE_TENANT_ID",
            "AZURE_CLIENT_ID",
            "AZURE_FEDERATED_TOKEN_FILE",
        };

        /// <summary>
        /// Valid workload identity settings, apart from the token file.
        /// </summary>
        private static Dictionary<string, string?> WorkloadIdentity(string? tokenFile) => new()
        {
            ["DOMAIN"] = "https://example.com/",
            ["AZURE_BLOB_STORAGE_ENDPOINT"] = Endpoint,
            ["AZURE_BLOB_STORAGE_CONTAINER_NAME"] = "$web",
            ["AZURE_TENANT_ID"] = "00000000-0000-0000-0000-000000000001",
            ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000002",
            ["AZURE_FEDERATED_TOKEN_FILE"] = tokenFile,
        };

        /// <summary>
        /// Creates an <see cref="Archiver"/> with exactly these variables
        /// set, and the rest unset, then puts every variable back.
        /// </summary>
        private static Exception? Construct(Dictionary<string, string?> values)
        {
            Dictionary<string, string?> originals = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);

            try
            {
                foreach (string name in Variables)
                    Environment.SetEnvironmentVariable(name, values.GetValueOrDefault(name));

                return Record.Exception(() => new Archiver());
            }
            finally
            {
                foreach (var (name, value) in originals)
                    Environment.SetEnvironmentVariable(name, value);
            }
        }

        private static string Fails(Dictionary<string, string?> values) =>
            Assert.IsType<InvalidOperationException>(Construct(values)).Message;

        [Fact]
        public void ConnectionString_IsAccepted()
        {
            Assert.Null(Construct(new()
            {
                ["DOMAIN"] = "https://example.com/",
                ["AZURE_BLOB_STORAGE_CONNECTION_STRING"] = "UseDevelopmentStorage=true",
                ["AZURE_BLOB_STORAGE_CONTAINER_NAME"] = "archive",
            }));
        }

        [Fact]
        public void EndpointWithWorkloadIdentity_IsAccepted()
        {
            string tokenFile = Path.GetTempFileName();

            try
            {
                Assert.Null(Construct(WorkloadIdentity(tokenFile)));
            }
            finally
            {
                File.Delete(tokenFile);
            }
        }

        [Fact]
        public void NoStorageAccount_IsReportedClearly()
        {
            Dictionary<string, string?> values = WorkloadIdentity("/token");
            values.Remove("AZURE_BLOB_STORAGE_ENDPOINT");

            string message = Fails(values);

            Assert.Contains("AZURE_BLOB_STORAGE_CONNECTION_STRING", message);
            Assert.Contains("AZURE_BLOB_STORAGE_ENDPOINT", message);
        }

        [Fact]
        public void ConnectionStringAndEndpoint_AreRejectedTogether()
        {
            Dictionary<string, string?> values = WorkloadIdentity("/token");
            values["AZURE_BLOB_STORAGE_CONNECTION_STRING"] = "UseDevelopmentStorage=true";

            string message = Fails(values);

            Assert.Contains("AZURE_BLOB_STORAGE_CONNECTION_STRING", message);
            Assert.Contains("AZURE_BLOB_STORAGE_ENDPOINT", message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(" ")]
        public void MissingContainerName_IsReportedClearly(string? containerName)
        {
            Dictionary<string, string?> values = WorkloadIdentity("/token");
            values["AZURE_BLOB_STORAGE_CONTAINER_NAME"] = containerName;

            Assert.Contains("AZURE_BLOB_STORAGE_CONTAINER_NAME", Fails(values));
        }

        [Theory]
        [InlineData("http://example.blob.core.windows.net/")]
        [InlineData("example.blob.core.windows.net")]
        public void EndpointThatIsNotHttps_IsReportedClearly(string endpoint)
        {
            Dictionary<string, string?> values = WorkloadIdentity("/token");
            values["AZURE_BLOB_STORAGE_ENDPOINT"] = endpoint;

            string message = Fails(values);

            Assert.Contains("AZURE_BLOB_STORAGE_ENDPOINT", message);
            Assert.Contains("https", message);
        }

        [Fact]
        public void EndpointWithoutWorkloadIdentity_ListsWhatIsMissing()
        {
            Dictionary<string, string?> values = WorkloadIdentity(null);
            values.Remove("AZURE_CLIENT_ID");

            string message = Fails(values);

            Assert.Contains("AZURE_CLIENT_ID", message);
            Assert.Contains("AZURE_FEDERATED_TOKEN_FILE", message);
            Assert.DoesNotContain("AZURE_TENANT_ID", message);
        }

        [Fact]
        public void TokenFileThatDoesNotExist_IsReportedClearly()
        {
            string tokenFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            Assert.Contains(tokenFile, Fails(WorkloadIdentity(tokenFile)));
        }
    }
}
