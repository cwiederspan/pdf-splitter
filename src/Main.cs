using System;
using System.IO;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;

namespace PdfSplitter.Function {

    public static class Main {

        [FunctionName("Main")]
        public static void Run(
            [BlobTrigger("originals/{name}", Connection = "AzureWebJobsStorage")]Stream myBlob, 
            string name,
            ILogger log
        ) {

            log.LogInformation($"C# Blob trigger function Processed blob\n Name:{name} \n Size: {myBlob.Length} Bytes");
        }
    }
}