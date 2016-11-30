using dumpling.db;
using dumpling.web.Storage;
using dumpling.web.telemetry;
using FileFormats;
using FileFormats.ELF;
using FileFormats.MachO;
using FileFormats.Minidump;
using FileFormats.PDB;
using FileFormats.PE;
using Microsoft.ApplicationInsights.Extensibility.Implementation;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Data.Entity.Validation;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;

namespace dumpling.web.Controllers
{
    public class DumplingApiController : ApiController
    {
        private const int BUFF_SIZE = 1024 * 4;

        [Route("api/client/{*filename}")]
        [HttpGet]
        public HttpResponseMessage GetClientTools(string filename)
        {
            string path = HttpContext.Current.Server.MapPath("~/Content/client/" + filename);

            HttpResponseMessage result = new HttpResponseMessage(HttpStatusCode.OK);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            result.Content = new StreamContent(stream);
            result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            result.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = filename };
            return result;
        }

        [Route("api/tools/debug")]
        [HttpGet]
        public async Task<HttpResponseMessage> GetDebugToolsAsync([FromUri] string os, CancellationToken cancelToken, [FromUri] string distro = null, [FromUri] string arch = null)
        {
            var blobName = string.Join("/", new string[] { os, distro, arch, "dbg.zip" }.Where(s => !string.IsNullOrEmpty(s)));

            var blob = await DumplingStorageClient.SupportContainer.GetBlobReferenceFromServerAsync(blobName, cancelToken);

            var response = await GetBlobRedirectAsync(blob, cancelToken);
            

            return response;
        }

        [Route("api/dumplings/{dumplingId}/manifest")]
        [HttpGet]
        public async Task<Dump> GetDumplingManifest(string dumplingId)
        {
            using (var op1 = new TrackedOperation("GetDumplingManifest"))
            {
                using (DumplingDb dumplingDb = new DumplingDb())
                {
                    Dump dump = null;

                    using (var op2 = new TrackedOperation("FindDumpAsync"))
                    {
                        dump = await dumplingDb.Dumps.FindAsync(dumplingId);
                    }

                    if (dump != null)
                    {
                        using (var op3 = new TrackedOperation("LoadDumpArtifactsAsync"))
                        {
                            await dumplingDb.Entry(dump).Collection(d => d.DumpArtifacts).LoadAsync();
                        }
                    }

                    return dump;
                }
            }
        }

        public class UploadDumpResponse
        {
            public string dumplingId { get; set; }
            public string[] refPaths { get; set; }
        }

        [Route("api/dumplings/{dumplingid}/properties")]
        [HttpPost]
        public async Task<HttpResponseMessage> UpdateDumpProperties(string dumplingid, [FromBody]JToken properties, CancellationToken cancelToken)
        {
            using (DumplingDb dumplingDb = new DumplingDb())
            {
                var dumpling = await dumplingDb.Dumps.FindAsync(cancelToken, dumplingid);

                //if the specified dump was not found throw an exception 
                if (dumpling == null)
                {
                    throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, "The given dumplingId is invalid"));
                }

                var propDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(properties.ToString());

                string failureHash = null;

                //if the properties contain FAILURE_HASH get the failure
                if(propDict.TryGetValue("FAILURE_HASH", out failureHash))
                {
                    //if the failure is not in the db yet try to add it
                    if(await dumplingDb.Failures.FindAsync(cancelToken, failureHash) == null)
                    {
                        try
                        {
                            dumplingDb.Failures.Add(new Failure() { FailureHash = failureHash });

                            await dumplingDb.SaveChangesAsync();
                        }
                        //swallow the validation exception if the failure was inserted by another request since we checked
                        catch(DbEntityValidationException e) 
                        {

                        }
                    }

                    dumpling.FailureHash = failureHash;
                }

                //update any properties which were pre-existing with the new value
                foreach (var existingProp in dumpling.Properties)
                {
                    string val = null;

                    if (propDict.TryGetValue(existingProp.Name, out val))
                    {
                        existingProp.Value = val;

                        propDict.Remove(existingProp.Name);
                    }
                }

                //add any properties which were not previously existsing 
                //(the existing keys have been removed in previous loop)
                foreach (var newProp in propDict)
                {
                    dumpling.Properties.Add(new Property() { Name = newProp.Key, Value = newProp.Value });
                }

                await dumplingDb.SaveChangesAsync();

                return Request.CreateResponse(HttpStatusCode.OK);
            }
        }

        [Route("api/dumplings/create/")]
        [HttpGet]
        public async Task<string> CreateDump([FromUri] string hash, [FromUri] string user, [FromUri] string displayName, CancellationToken cancelToken)
        {
            //if the specified hash is not formatted properly throw an exception
            if (!ValidateHashFormat(hash))
            {
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, "The specified hash is improperly formatted"));
            }

            using (DumplingDb dumplingDb = new DumplingDb())
            {
                var dumpling = await dumplingDb.Dumps.FindAsync(cancelToken, hash);

                if (dumpling != null)
                {
                    return hash;
                }

                dumpling = new Dump() { DumpId = hash, User = user, DisplayName = displayName, DumpTime = DateTime.UtcNow, Os = OS.Unknown };

                dumplingDb.Dumps.Add(dumpling);

                try
                {
                    await dumplingDb.SaveChangesAsync();
                }
                catch (DbEntityValidationException)
                {
                    dumpling = await dumplingDb.Dumps.FindAsync(cancelToken, hash);

                    //if the specified dump was not found throw an exception 
                    if (dumpling != null)
                    {
                        return hash;
                    }

                    throw;
                }

                return hash;
            }

        }

        [Route("api/dumplings/uploads/")]
        [HttpPost]
        public async Task<string> UploadDump([FromUri] string hash, [FromUri] string localpath, CancellationToken cancelToken)
        {
            await StoreArtifactContentAsync(this.Request.Content, hash, hash, localpath, cancelToken);

            return hash;
        }

        [Route("api/dumplings/{dumplingid}/artifacts/uploads/")]
        [HttpPost]
        public async Task<string> UploadArtifact(string dumplingid, [FromUri] string hash, [FromUri] string localpath, CancellationToken cancelToken)
        {
            await StoreArtifactContentAsync(this.Request.Content, hash, dumplingid, localpath, cancelToken);

            return hash;
        }

        [Route("api/artifacts/uploads")]
        [HttpPost]
        public async Task<string> UploadArtifact([FromUri] string hash, [FromUri] string localpath, CancellationToken cancelToken)
        {
            await StoreArtifactContentAsync(this.Request.Content, hash, null, localpath, cancelToken);

            return hash;
        }

        [Route("api/artifacts/{hash}")]
        [HttpGet]
        public async Task<HttpResponseMessage> DownloadArtifact(string hash, CancellationToken cancelToken)
        {
            using (var dumplingDb = new DumplingDb())
            {
                var artifact = await dumplingDb.Artifacts.FindAsync(hash);

                return await GetArtifactRedirectAsync(artifact, cancelToken);
            }
        }

        [Route("api/artifacts/index/{*index}")]
        [HttpGet]
        public async Task<HttpResponseMessage> DownloadIndexedArtifact(string index, CancellationToken cancelToken)
        {
            Artifact artifact = null;

            using (var dumplingDb = new DumplingDb())
            {
                var artifactIndex = await dumplingDb.ArtifactIndexes.FindAsync(index);

                artifact = artifactIndex?.Artifact;
            }

            return await GetArtifactRedirectAsync(artifact, cancelToken);
        }

        [Route("api/dumplings/archived/{dumplingid}")]
        [HttpGet]
        public async Task<HttpResponseMessage> DownloadArchivedDump(string dumplingid, CancellationToken cancelToken)
        {
            using (var dumplingDb = new DumplingDb())
            {
                var dump = await dumplingDb.Dumps.FindAsync(cancelToken, dumplingid);

                if (dump == null)
                {
                    return Request.CreateResponse(HttpStatusCode.NotFound);
                }
                var archiveTasks = new List<Task>();
                dumplingDb.Entry(dump).Collection(d => d.Properties).Load();
                var fileName = dump.DisplayName + ".zip";
                var tempFile = CreateTempFile();

                try
                {
                    using (var zipArchive = new ZipArchive(tempFile, ZipArchiveMode.Create, true))
                    using (var archiveLock = new SemaphoreSlim(1, 1))
                    {
                        //find all the artifacts associated with the dump
                        foreach (var dumpArtifact in dump.DumpArtifacts.Where(da => da.Hash != null))
                        {
                            await dumplingDb.Entry(dumpArtifact).Reference(d => d.Artifact).LoadAsync(cancelToken);

                            archiveTasks.Add(DownloadArtifactToArchiveAsync(dumpArtifact, zipArchive, archiveLock, cancelToken));
                        }

                        await Task.WhenAll(archiveTasks.ToArray());

                        await tempFile.FlushAsync();
                    }

                    await tempFile.FlushAsync();

                    tempFile.Position = 0;
                    HttpResponseMessage result = new HttpResponseMessage(HttpStatusCode.OK);
                    result.Content = new StreamContent(tempFile);
                    result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                    result.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment");
                    result.Content.Headers.ContentDisposition.FileName = fileName;
                    return result;
                }
                catch
                {
                    tempFile.Dispose();

                    throw;
                }
            }

        }
        
        private async Task DownloadArtifactToArchiveAsync(DumpArtifact dumpArtifact, ZipArchive archive, SemaphoreSlim archiveLock, CancellationToken cancelToken)
        {
            if (!cancelToken.IsCancellationRequested)
            {
                var blob = DumplingStorageClient.BlobClient.GetBlobReferenceFromServer(new Uri(dumpArtifact.Artifact.Url));

                //download the compressed dump artifact to a temp file
                using (var tempStream = CreateTempFile())
                {

                    using (var compStream = CreateTempFile())
                    {
                        await blob.DownloadToStreamAsync(compStream, cancelToken);

                        using (var gunzipStream = new GZipStream(compStream, CompressionMode.Decompress, false))
                        {
                            await gunzipStream.CopyToAsync(tempStream);
                        }

                        await tempStream.FlushAsync();
                    }

                    tempStream.Position = 0;

                    await archiveLock.WaitAsync(cancelToken);

                    if (!cancelToken.IsCancellationRequested)
                    {
                        try
                        {
                            var entry = archive.CreateEntry(FixupLocalPath(dumpArtifact.LocalPath));

                            using (var entryStream = entry.Open())
                            {
                                await tempStream.CopyToAsync(entryStream);

                                await entryStream.FlushAsync();
                            }
                        }
                        finally
                        {
                            archiveLock.Release();
                        }
                    }
                }
            }
        }
        
        //removes the root from the local path and change to posix path separator (for win paths changes root c:\path to c/path)
        private static string FixupLocalPath(string localPath)
        {
            var path = localPath.Replace(":", string.Empty).Replace('\\', '/').TrimStart('/');

            return path;
        }

        private async Task<HttpResponseMessage> GetArtifactRedirectAsync(Artifact artifact, CancellationToken cancelToken)
        {
            if (artifact == null)
            {
                return Request.CreateResponse(HttpStatusCode.NotFound);
            }

            var blob = await DumplingStorageClient.BlobClient.GetBlobReferenceFromServerAsync(new Uri(artifact.Url), cancelToken);

            var httpResponse = await GetBlobRedirectAsync(blob, cancelToken);

            httpResponse.Headers.Add("dumpling-filename", artifact.FileName);

            return httpResponse;
        }

        private async Task<HttpResponseMessage> GetBlobRedirectAsync(ICloudBlob blob, CancellationToken cancelToken)
        {
            if(!await blob.ExistsAsync(cancelToken))
            {
                return Request.CreateResponse(HttpStatusCode.NotFound);
            }

            var sasConstraints = new SharedAccessBlobPolicy()
            {
                SharedAccessStartTime = DateTime.UtcNow.AddMinutes(-1),
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(1),
                Permissions = SharedAccessBlobPermissions.Read
            };

            var blobToken = blob.GetSharedAccessSignature(sasConstraints);

            var tempAccessUrl = blob.Uri.AbsoluteUri + blobToken;

            var httpResponse = await ((IHttpActionResult)this.Redirect(tempAccessUrl)).ExecuteAsync(cancelToken);
            
            return httpResponse;
        }

        private static bool ValidateHashFormat(string hash)
        {
            if (hash.Length != 40)
            {
                return false;
            }

            foreach(var c in hash.Select(ch => char.ToLowerInvariant(ch)))
            {
                if(!(char.IsDigit(c) || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        private string GetOperationToken()
        {
            return Guid.NewGuid().ToString("N");
        }
        
        private async Task StoreArtifactContentAsync(HttpContent content, string hash, string dumpId, string localPath, CancellationToken cancelToken)
        {
            //if the specified hash is not formatted properly throw an exception
            if (!ValidateHashFormat(hash))
            {
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, "The specified hash is improperly formatted"));
            }

            using (DumplingDb dumplingDb = new DumplingDb())
            {
                var artifact = await AddArtifactToDbAsync(dumplingDb, hash, localPath, cancelToken);

                //if the artifact didn't already exist and we added it to the db upload the file
                if (artifact != null)
                {
                    using (var uploaded = await UploadContentValidateHashAsync(content, hash, cancelToken))
                    {
                        artifact.CompressedSize = uploaded.Length;

                        uploaded.Position = 0;

                        artifact.Url = await DumplingStorageClient.StoreArtifactAsync(uploaded, hash, artifact.FileName + ".gz", cancelToken);

                        await dumplingDb.SaveChangesAsync(cancelToken);
                    }
                }

                //if a dumpId was specified add the dumpartifact entry
                if (dumpId != null)
                {
                    await AddDumpArtifactToDbAsync(dumplingDb, dumpId, localPath, hash, cancelToken);
                }

            }
        }

        private async Task<Artifact> AddArtifactToDbAsync(DumplingDb dumplingDb, string hash, string localPath, CancellationToken cancelToken)
        {
            using (var opTracker = new TrackedOperation("AddArtifactToDbAsync"))
            {
                var artifact = new Artifact()
                {
                    Hash = hash,
                    FileName = Path.GetFileName(localPath),
                    UploadTime = DateTime.UtcNow,
                    Format = ArtifactFormat.Unknown,
                    Uuid = null
                };

                return await dumplingDb.TryAddAsync(artifact, cancelToken) ? artifact : null;
            }
        }

        private async Task<DumpArtifact> AddDumpArtifactToDbAsync(DumplingDb dumplingDb, string dumpId, string localPath, string hash, CancellationToken cancelToken)
        {
            using (var opTracker = new TrackedOperation("AddDumpArtifactToDbAsync"))
            {
                //if the specified dumpId is not valid throw an exception
                if (await dumplingDb.Dumps.FindAsync(cancelToken, dumpId) == null)
                {
                    throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, "The specified dumpling id is invalid."));
                }

                var dumpArtifact = new DumpArtifact()
                {
                    DumpId = dumpId,
                    LocalPath = localPath,
                    DebugCritical = true,
                    ExecutableImage = false,
                    Hash = hash
                };

                dumplingDb.DumpArtifacts.AddOrUpdate(dumpArtifact);

                await dumplingDb.SaveChangesAsync(cancelToken);

                return dumpArtifact;
            }
        }

        private async Task<Stream> UploadContentValidateHashAsync(HttpContent content, string expectedHash, CancellationToken cancelToken)
        {
            using (var opTracker = new TrackedOperation("UploadConentValidateHashAsync"))
            {
                string hash = null;

                long length = 0;

                using (var contentStream = await Request.Content.ReadAsStreamAsync())
                {
                    var operationMetrics = new Dictionary<string, double>() { { "FileLength", 0 } };

                    //we don't open the temp file in a using statement b/c we need to return to the caller
                    Stream fileStream = CreateTempFile();

                    try
                    {
                        using (var sha1 = SHA1.Create())
                        {
                            var buff = new byte[BUFF_SIZE];

                            int cbyte;

                            while ((cbyte = await contentStream.ReadAsync(buff, 0, buff.Length)) > 0)
                            {
                                cancelToken.ThrowIfCancellationRequested();

                                length += cbyte;

                                sha1.TransformBlock(buff, 0, cbyte, buff, 0);

                                await fileStream.WriteAsync(buff, 0, cbyte);
                            }

                            await fileStream.FlushAsync();

                            sha1.TransformFinalBlock(buff, 0, 0);

                            hash = string.Concat(sha1.Hash.Select(b => b.ToString("x2"))).ToLowerInvariant();

                            operationMetrics["FileLength"] = Convert.ToDouble(length);
                        }
                    }
                    //if an exception was thrown while uploading delete the file and rethrow to prevent leaking the incomplete file
                    catch (Exception)
                    {
                        fileStream.Dispose();

                        throw;
                    }

                    //if the hash doesn't match close the file so it deletes and throw an exception
                    if (hash != expectedHash)
                    {
                        fileStream.Dispose();

                        throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, "The specified hash does not match hash of the uploaded content."));
                    }

                    return fileStream;
                }
            }
        }

        private static Stream CreateTempFile(string path = null)
        {
            path = path ?? Path.GetTempFileName();

            string root = HttpContext.Current.Server.MapPath("~/App_Data/temp");

            string tempPath = Path.Combine(root, path);

            if(!Directory.Exists(Path.GetDirectoryName(tempPath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath));
            }

            //this file is not disposed of here b/c it is deleted on close
            //callers of this method are responsible for disposing the file
            return File.Create(tempPath, BUFF_SIZE, FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        }
    }
}
