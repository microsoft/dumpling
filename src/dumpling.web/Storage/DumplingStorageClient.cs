using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web;

namespace dumpling.web.Storage
{
    public class DumplingStorageClient
    {
        private static Lazy<DumplingStorageClient> s_singleton = new Lazy<DumplingStorageClient>(() => new DumplingStorageClient());

        private static DumplingStorageClient _instance { get { return s_singleton.Value; } }

        private DumplingStorageClient()
        {
            Initialize();

        }

        private CloudBlobClient _blobClient;

        private CloudBlobContainer _artifactContainer;

        public static CloudBlobClient BlobClient
        {
            get
            {
                return _instance._blobClient;
            }
        }

        public static CloudBlobContainer ArtifactContainer
        {
            get
            {
                return _instance._artifactContainer;
            }
        }

        private void Initialize()
        {
            _blobClient = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["StorageConnectionString"]).CreateCloudBlobClient();

            _artifactContainer = _blobClient.GetContainerReference(ConfigurationManager.AppSettings["ArtifactContainer"]);

            _artifactContainer.CreateIfNotExistsAsync().GetAwaiter().GetResult();
        }

        public static async Task<string> StoreArtifactAsync(Stream stream, string hash, string fileName)
        {
            var blob = _instance._artifactContainer.GetBlockBlobReference(hash + "/" + fileName);

            await blob.UploadFromStreamAsync(stream);

            return blob.Uri.ToString();
        }
    }
}