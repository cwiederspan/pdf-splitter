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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

using Newtonsoft.Json;

using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfSplitter.Function {

    public class Splitter {

        private readonly string CognitiveServicesEndpoint;

        private readonly string CognitiveServicesApiKey;

        public Splitter() {

            this.CognitiveServicesEndpoint = System.Environment.GetEnvironmentVariable("CognitiveServicesEndpoint", EnvironmentVariableTarget.Process);
            this.CognitiveServicesApiKey = System.Environment.GetEnvironmentVariable("CognitiveServicesApiKey", EnvironmentVariableTarget.Process);
        }

        [FunctionName("Split")]
        public async Task SplitAsync(
            [BlobTrigger("originals/{name}", Connection = "AzureWebJobsStorage")] Stream stream, 
            string name,
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

            this.SplitFile(fileContent, splitterPages, blankPages);
        }

        private async Task<string> PostBatchReadRequestAsync(Byte[] content) {

            var resultUrl = String.Empty;

            using (var client = new HttpClient()) {

                // Request headers
                client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", this.CognitiveServicesApiKey);

                var uri = $"{this.CognitiveServicesEndpoint}/vision/v2.0/read/core/asyncBatchAnalyze";

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
                client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", this.CognitiveServicesApiKey);

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

        private void SplitFile(Byte[] original, IList<int> splitterPages, IList<int> blankPages) { 

            using (var stream = new MemoryStream(original)) {

                var inputDocument = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                int count = inputDocument.PageCount;
                List<MemoryStream> msList = new List<MemoryStream>();

                var newDocument = new PdfDocument();

                for (int idx = 0; idx < count; idx++) {

                    if (splitterPages.Contains(idx + 1) == true) {

                        // This is a splitter page, or it is end of file

                        var newFilename = $"C:\\Temp\\split-{Guid.NewGuid().ToString().Substring(0, 8)}.pdf";
                        newDocument.Save(newFilename);

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
                var lastFilename = $"C:\\Temp\\split-{Guid.NewGuid().ToString().Substring(0, 8)}.pdf";
                newDocument.Save(lastFilename);
            }
        }
    }
}