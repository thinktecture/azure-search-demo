using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Search;
using Microsoft.Azure.Search.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Index = Microsoft.Azure.Search.Models.Index;

namespace AzureSearchServerless
{
    public static class SearchIndex
    {
        [FunctionName("Rebuild")]
        public static async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)]
            HttpRequest req, ILogger log, ExecutionContext executionContext)
        {
            log.LogInformation("Start rebuilding search index");

            using var httpClient = new HttpClient();
            log.LogInformation("Retrieving data urls.");
            var response = await httpClient.GetAsync(Environment.GetEnvironmentVariable("DataUrl"));
            var urlList = JsonConvert.DeserializeObject<List<string>>(await response.Content.ReadAsStringAsync());
            var blobContainerClient =
                new BlobContainerClient(Environment.GetEnvironmentVariable("AzureBlogStorageConnectionString"),
                    "index-storage");


            log.LogInformation("Clear blob storage");
            await foreach (var blob in blobContainerClient.GetBlobsAsync())
                await blobContainerClient.DeleteBlobAsync(blob.Name, DeleteSnapshotsOption.IncludeSnapshots);

            log.LogInformation("Upload data files into blob storage.");
            foreach (var detailUrl in urlList)
            {
                log.LogInformation($"Retrieving data for url: {detailUrl}");
                var detailResponse = await httpClient.GetAsync(detailUrl);
                var detailResponseStream = await detailResponse.Content.ReadAsStreamAsync();

                using var md5 = MD5.Create();
                var hashBytes =
                    md5.ComputeHash(Encoding.ASCII.GetBytes(await detailResponse.Content.ReadAsStringAsync()));
                var md5Hash = new StringBuilder();
                foreach (var t in hashBytes)
                {
                    md5Hash.Append(t.ToString("X2"));
                }

                try
                {
                    await blobContainerClient.UploadBlobAsync(md5Hash + ".json",
                        detailResponseStream);
                }
                catch (RequestFailedException e)
                {
                    log.LogInformation("Blob didn't change...");
                }
            }

            var functionPath = Directory.GetParent(executionContext.FunctionDirectory).FullName;

            log.LogInformation("Reading SearchIndex configuration.");
            var indexJson = await File.ReadAllTextAsync(
                $"{functionPath}/index/index.search.json");

            var indexConfig = JsonConvert.DeserializeObject<Index>(indexJson);
            var serviceClient = new SearchServiceClient(Environment.GetEnvironmentVariable("SearchServiceName"),
                new SearchCredentials(Environment.GetEnvironmentVariable("SearchApiKey")));

            try
            {
                log.LogInformation("Clear old index.");
                if (await serviceClient.Indexes.ExistsAsync(indexConfig.Name))
                    await serviceClient.Indexes.DeleteAsync(indexConfig.Name);

                log.LogInformation("Create new index.");
                await serviceClient.Indexes.CreateAsync(indexConfig);

                log.LogInformation("Read indexer configuration");
                var indexerJson = await File.ReadAllTextAsync($"{functionPath}/index/indexer.search.json");
                var indexerConfig = JsonConvert.DeserializeObject<Indexer>(indexerJson);
                if (await serviceClient.Indexers.ExistsAsync(indexerConfig.Name))
                    await serviceClient.Indexers.DeleteAsync(indexerConfig.Name);

                await serviceClient.Indexers.CreateAsync(indexerConfig);

                // Exception is thrown based on the lowest pricing tier if I run it below every 180 seconds
                log.LogInformation("Run indexer");
                await serviceClient.Indexers.RunAsync(indexerConfig.Name);
            }
            catch (Exception e)
            {
                log.LogInformation(e.ToString());
            }

            return new OkObjectResult("Index successfully rebuilt.");
        }
    }
}