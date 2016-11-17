using dumpling.web.telemetry;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
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
        private CloudBlobContainer _supportContainer;

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

        public static CloudBlobContainer SupportContainer
        {
            get
            {
                return _instance._supportContainer;
            }
        }

        private void Initialize()
        {
            _blobClient = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["StorageConnectionString"]).CreateCloudBlobClient();

            _artifactContainer = _blobClient.GetContainerReference(ConfigurationManager.AppSettings["ArtifactContainer"]);

            _supportContainer = _blobClient.GetContainerReference(ConfigurationManager.AppSettings["SupportContainer"]);

            _artifactContainer.CreateIfNotExistsAsync().GetAwaiter().GetResult();

            _supportContainer.CreateIfNotExistsAsync().GetAwaiter().GetResult();
        }

        public static async Task<string> StoreArtifactAsync(Stream stream, string hash, string fileName, CancellationToken cancelToken)
        {
            using (var opTracker = new TrackedOperation("StoreArtifactBlob", new Dictionary<string, string>() { { "Hash", hash } }))
            {
                var blob = _instance._artifactContainer.GetBlockBlobReference(hash + "/" + fileName);

                await blob.UploadFromStreamAsync(stream, cancelToken);

                return blob.Uri.ToString();
            }
        }

        public static async Task<bool> DeleteArtifactAsync(string hash, string fileName, CancellationToken cancelToken)
        {
            using (var opTracker = new TrackedOperation("DeleteArtifactBlob", new Dictionary<string, string>() { { "Hash", hash } }))
            {
                var blob = _instance._artifactContainer.GetBlockBlobReference(hash + "/" + fileName);

                return await blob.DeleteIfExistsAsync(cancelToken);
            }
        }
    }
}