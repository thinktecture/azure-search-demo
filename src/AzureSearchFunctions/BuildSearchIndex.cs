using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

namespace AzureSearchFunctions
{
    public static class SearchIndex
    {

        [FunctionName("Rebuild")]
        public static async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "POST", Route = "rebuild-index/{count}")] 
            HttpRequest req,
            int count,
            ILogger log, ExecutionContext executionContext)
        {
            if (count < 1)
            {
                return new BadRequestResult();
            }

            var functionRootPath = Directory.GetParent(executionContext.FunctionDirectory).FullName;

            // update Azure Blob Storage with external data (Pokemon Infos)
            await UpdateAzureBlobStorageAsync(count, log);

            // Get Azure Cognitive Search Index Configuration
            var searchIndexConfig = await GetConfigAsync<Microsoft.Azure.Search.Models.Index>(functionRootPath, "index.json", log);
            // Get Indexer Configuration for Azure Blob Storage
            var searchIndexerConfig = await GetConfigAsync<Indexer>(functionRootPath, "indexer.json", log);

            if (searchIndexConfig == null)
            {
                log.LogError("Configuration for Azure Cognitive Search Index not generated");
                return new StatusCodeResult(500);
            }

            if (searchIndexerConfig == null)
            {
                log.LogError("Configuration for Blob Storage Indexer not generated");
                return new StatusCodeResult(500);
            }

            try
            {
                var serviceClient = new SearchServiceClient(
                    Environment.GetEnvironmentVariable("SEARCH_SERVICE_NAME"),
                    new SearchCredentials(Environment.GetEnvironmentVariable("SEARCH_API_KEY")));

                log.LogInformation("Delete existing Index");
                if (await serviceClient.Indexes.ExistsAsync(searchIndexConfig.Name))
                    await serviceClient.Indexes.DeleteAsync(searchIndexConfig.Name);

                log.LogInformation("Create new Index");
                await serviceClient.Indexes.CreateAsync(searchIndexConfig);


                log.LogInformation("Delete existing indexer");
                if (await serviceClient.Indexers.ExistsAsync(searchIndexerConfig.Name))
                    await serviceClient.Indexers.DeleteAsync(searchIndexerConfig.Name);
                
                log.LogInformation("Delete existing data source");
                if (await serviceClient.DataSources.ExistsAsync(searchIndexerConfig.DataSourceName))
                    await serviceClient.DataSources.DeleteAsync(searchIndexerConfig.DataSourceName);

                log.LogInformation("Create new data source");
                await serviceClient.DataSources.CreateAsync(
                    DataSource.AzureBlobStorage(searchIndexerConfig.DataSourceName,
                        Environment.GetEnvironmentVariable("AzureBlogStorageConnectionString"),
                        Environment.GetEnvironmentVariable("IndexContainerName")));

                log.LogInformation("Create new Indexer");
                await serviceClient.Indexers.CreateAsync(searchIndexerConfig);

                // Exception is thrown on the lowest pricing tier
                // if indexer run it requested mutliple times witihn 180sec.
                log.LogInformation("Run indexer");
                await serviceClient.Indexers.RunAsync(searchIndexerConfig.Name);
            }
            catch (Exception e)
            {
                log.LogError(e, "Error while building new Index");
            }

            return new OkObjectResult("Index successfully re-created");
        }

        private static IEnumerable<string> GetDataUrls(int count)
        {
            return Enumerable.Range(1, count).Select(num => $"https://pokeapi.co/api/v2/pokemon/{num}");
        }

        public static string GetHashBasedFileName(string data)
        {
            using var md5 = MD5.Create();
            var hashBytes = md5.ComputeHash(Encoding.ASCII.GetBytes(data));
            var md5Hash = new StringBuilder();
            foreach (var t in hashBytes)
            {
                md5Hash.Append(t.ToString("X2"));
            }
            return $"{md5Hash}.json";
        }

        private static async Task UpdateAzureBlobStorageAsync(int count, ILogger logger)
        {
            logger.LogInformation("Start rebuilding search index");
            

            logger.LogInformation("Retrieving data urls.");
            var dataUrls = GetDataUrls(count);
            // container name "index-storage"
            var blobContainerClient = new BlobContainerClient(
                Environment.GetEnvironmentVariable("AzureBlogStorageConnectionString"),
                Environment.GetEnvironmentVariable("IndexContainerName"));


            logger.LogInformation("Clear blob storage");
            await foreach (var blob in blobContainerClient.GetBlobsAsync())
                await blobContainerClient.DeleteBlobAsync(blob.Name, DeleteSnapshotsOption.IncludeSnapshots);

            logger.LogInformation("Upload data files into blob storage.");
            dataUrls.ToList().ForEach(async dataUrl =>
            {
                using var httpClient = new HttpClient();
                logger.LogInformation($"Retrieving data for url: {dataUrl}");
                var response = await httpClient.GetAsync(dataUrl);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    logger.LogInformation($"Received response with status code {response.StatusCode} for request with url {dataUrl}. skipping...");
                    return;
                }
                try
                {
                    await blobContainerClient.UploadBlobAsync($"{Guid.NewGuid()}.json", await response.Content.ReadAsStreamAsync());
                }
                catch (RequestFailedException)
                {
                    logger.LogInformation("Blob did not change, skipping upload");
                }
            });
        }

        private static async Task<T> GetConfigAsync<T>(string functionsRootPath, string fileName, ILogger logger)
        {
            logger.LogInformation($"Loading configuration for {typeof(T).Name} from local Filesystem");
            try
            {
                var configPath = Path.Join(functionsRootPath, "AzureCognitiveSearchConfigurations", fileName);
                var content = await File.ReadAllTextAsync(configPath);
                return JsonConvert.DeserializeObject<T>(content);
            }
            catch (Exception)
            {
                return default(T);
            }
        }
    }
}
