using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.WindowsAzure.Storage.Blob;

using Newtonsoft.Json;

using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfSplitter.Function {

    public class Splitter {

        private static string COGSVC_ENDPOINT; // CognitiveServicesEndpoint;

        private static string COGSVC_API_KEY; // CognitiveServicesApiKey;

        static Splitter() {
            
            // This is required to avoid the "No data is available for encoding 1252" exception when saving the PdfDocument
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            COGSVC_ENDPOINT = System.Environment.GetEnvironmentVariable("CognitiveServicesEndpoint", EnvironmentVariableTarget.Process);
            COGSVC_API_KEY = System.Environment.GetEnvironmentVariable("CognitiveServicesApiKey", EnvironmentVariableTarget.Process);
        }

        /*
        [FunctionName("SplitBlob")]
        public async Task SplitBlobAsync(
            [BlobTrigger("originals/{name}", Connection = "AzureWebJobsStorage")] Stream stream, 
            string name,
            [Blob("output", Connection = "AzureWebJobsStorage")] CloudBlobContainer outputContainer,
            ILogger log
        ) {

            // Read in the stream to memory
            var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            var fileContent = memoryStream.ToArray();

            var resourceUrl = await this.PostBatchReadRequestAsync(fileContent);

            var batchResult = await this.RetrieveBatchReadResponseAsync(resourceUrl);

            var splitterPages = this.FindSplitterPages(batchResult);

            var blankPages = this.FindBlankPages(batchResult);

            var newFiles = await this.SplitFileAsync(outputContainer, fileContent, splitterPages, blankPages);
        }
        */

        [FunctionName("SplitUpload")]
        public async Task<Result> SplitUploadAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, methods: "post")] HttpRequestMessage request,
            [Blob("output", Connection = "AzureWebJobsStorage")] CloudBlobContainer outputContainer,
            ILogger log
        ) {

            // Read in the stream to memory
            var memoryStream = new MemoryStream();
            await request.Content.CopyToAsync(memoryStream);
            var fileContent = memoryStream.ToArray();

            var resourceUrl = await this.PostBatchReadRequestAsync(fileContent);

            var batchResult = await this.RetrieveBatchReadResponseAsync(resourceUrl);

            var splitterPages = this.FindSplitterPages(batchResult);

            var blankPages = this.FindBlankPages(batchResult);

            var newFiles = await this.SplitFileAsync(outputContainer, fileContent, splitterPages, blankPages);

            return new Result(newFiles);
        }

        private async Task<string> PostBatchReadRequestAsync(Byte[] content) {

            var resultUrl = String.Empty;

            using (var client = new HttpClient()) {

                // Request headers
                client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", COGSVC_API_KEY);

                var uri = $"{COGSVC_ENDPOINT}/vision/v2.0/read/core/asyncBatchAnalyze";

                var binaryContent = new ByteArrayContent(content);
                binaryContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                var processRespone = await client.PostAsync(uri, binaryContent);

                resultUrl = processRespone.Headers.Single(h => h.Key == "Operation-Location").Value.Single();
            }

            return resultUrl;
        }

        private async Task<BatchResult> RetrieveBatchReadResponseAsync(string resourceUrl) {

            BatchResult batchResult = null;

            using (var client = new HttpClient()) {

                // Request headers
                client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", COGSVC_API_KEY);

                do {

                    Thread.Sleep(TimeSpan.FromSeconds(2));

                    var response = await client.GetStringAsync(resourceUrl);

                    var result = JsonConvert.DeserializeObject<BatchResult>(response);

                    batchResult = result.status == "Succeeded" ? result : null;

                } while (batchResult == null);

                // Return the result
                return batchResult;
            }
        }

        private IList<int> FindSplitterPages(BatchResult batch) {

            var pages = batch.recognitionResults
                .Where(r => r.lines.Any(l => l.text.ToUpper().Contains("SEPARATOR") && l.text.ToUpper().Contains("INVOICE")))
                .Distinct()
                .Select(r => r.page)
                .ToList();

            return pages;
        }

        private IList<int> FindBlankPages(BatchResult batch) {

            var pages = batch.recognitionResults
                .Where(r => r.lines.Length == 0)
                .Distinct()
                .Select(r => r.page)
                .ToList();

            return pages;
        }

        private async Task<IList<string>> SplitFileAsync(CloudBlobContainer container, Byte[] original, IList<int> splitterPages, IList<int> blankPages) { 

            var outputFiles = new List<string>();
            var tempFile = String.Empty;

            using (var stream = new MemoryStream(original)) {

                var inputDocument = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                int count = inputDocument.PageCount;

                var newDocument = new PdfDocument();

                for (int idx = 0; idx < count; idx++) {

                    if (splitterPages.Contains(idx + 1) == true) {

                        // This is a splitter page, or it is end of file

                        // Save the document to a byte array
                        tempFile = await this.SaveToBlobStorageAsync(container, newDocument);
                        outputFiles.Add(tempFile);

                        // If this is the last page, then don't do anything
                        if (idx + 1 < count) {
                            newDocument = new PdfDocument();
                        }
                    }
                    else if (blankPages.Contains(idx + 1) == false) {

                        // This is not a splitter page, so add it to the document
                        newDocument.AddPage(inputDocument.Pages[idx]);
                    }
                }

                // Make sure the last document is saved
                tempFile = await this.SaveToBlobStorageAsync(container, newDocument);
                outputFiles.Add(tempFile);
            }

            return outputFiles;
        }

        private async Task<string> SaveToBlobStorageAsync(CloudBlobContainer container, PdfDocument document) {

            var filename = $"split-{DateTime.Now.ToString("yyyyMMddhhmmss")}-{Guid.NewGuid().ToString().Substring(0, 4)}.pdf";
            
            // Creating an empty file pointer
            var outputBlob = container.GetBlockBlobReference(filename);

            using (var stream = new MemoryStream()) {

                document.Save(stream);
                await outputBlob.UploadFromStreamAsync(stream);
            }

            var sasBlobToken = outputBlob.GetSharedAccessSignature(new SharedAccessBlobPolicy() {
                SharedAccessExpiryTime = DateTime.UtcNow.AddMinutes(5),
                Permissions = SharedAccessBlobPermissions.Read
            });

            return outputBlob.Uri + sasBlobToken;
        }
    }
}