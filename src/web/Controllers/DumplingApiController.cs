using dumpling.db;
using dumpling.web.Storage;
using FileFormats;
using FileFormats.ELF;
using FileFormats.MachO;
using FileFormats.Minidump;
using FileFormats.PDB;
using FileFormats.PE;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.Entity.Infrastructure;
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
            var stream = new FileStream(path, FileMode.Open);
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
            using (DumplingDb dumplingDb = new DumplingDb())
            {
                var dump = await dumplingDb.Dumps.FindAsync(dumplingId);

                if(dump != null)
                {
                    await dumplingDb.Entry(dump).Reference(d => d.Failure).LoadAsync();

                    await dumplingDb.Entry(dump).Collection(d => d.DumpArtifacts).LoadAsync();

                    foreach (var dumpart in dump.DumpArtifacts)
                    {
                        if (dumpart.Hash != null)
                        {
                            await dumplingDb.Entry(dumpart).Reference(da => da.Artifact).LoadAsync();
                        }
                    }

                    await dumplingDb.Entry(dump).Collection(d => d.Properties).LoadAsync();
                }

                return dump;
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

        [Route("api/dumplings/uploads/")]
        [HttpPost]
        public async Task<UploadDumpResponse> UploadDump([FromUri] string hash, [FromUri] string localpath, [FromUri] string origin, [FromUri] string displayName, CancellationToken cancelToken)
        {
            using (DumplingDb dumplingDb = new DumplingDb())
            {
                var dumpling = await dumplingDb.Dumps.FindAsync(cancelToken, hash);

                //if the specified dump was not found throw an exception 
                if (dumpling != null)
                {
                    return new UploadDumpResponse() { dumplingId = dumpling.DumpId, refPaths = new string[] { } };
                }

                var clientFilesNeeded = new List<string>();

                dumpling = new Dump() { DumpId = hash, User = origin, DisplayName = displayName, DumpTime = DateTime.UtcNow, Os = OS.Unknown };

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
                        return new UploadDumpResponse() { dumplingId = dumpling.DumpId, refPaths = new string[] { } };
                    }

                    throw;
                }

                using (var uploader = new DumpArtifactUploader())
                {
                    await UploadDumpArtifactAsync(uploader, dumplingDb, dumpling, hash, localpath, true, cancelToken);

                    dumpling.Os = uploader.DumpOS;

                    foreach (var dumpArtifact in uploader.GetLoadedModules(dumpling.DumpId))
                    {
                        var artifactIndex = await dumplingDb.ArtifactIndexes.FindAsync(dumpArtifact.Index);

                        if (artifactIndex != null)
                        {
                            dumpArtifact.Hash = artifactIndex.Hash;
                        }
                        else
                        {
                            clientFilesNeeded.Add(dumpArtifact.LocalPath);
                        }

                        dumpling.DumpArtifacts.Add(dumpArtifact);
                    }

                    await dumplingDb.SaveChangesAsync();
                }

                return new UploadDumpResponse() { dumplingId = dumpling.DumpId, refPaths = clientFilesNeeded.ToArray() };
            }
        }

        [Route("api/dumplings/{dumplingid}/artifacts/uploads/")]
        [HttpPost]
        public async Task UploadArtifact(string dumplingid, [FromUri] string hash, [FromUri] string localpath, CancellationToken cancelToken)
        {
            using (DumplingDb dumplingDb = new DumplingDb())
            {
                var dumpling = await dumplingDb.Dumps.FindAsync(cancelToken, dumplingid);

                //if the specified dump was not found throw an exception 
                if (dumpling == null)
                {
                    throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, "The given dumplingId is invalid"));
                }

                using (var uploader = new ArtifactUploader())
                {
                    var artifact = await UploadDumpArtifactAsync(uploader, dumplingDb, dumpling, hash, localpath, false, cancelToken);
                }

                await dumplingDb.SaveChangesAsync();
            }
        }

        [Route("api/artifacts/uploads")]
        [HttpPost]
        public async Task UploadArtifact([FromUri] string hash, [FromUri] string localpath, CancellationToken cancelToken)
        {
            using (DumplingDb dumplingDb = new DumplingDb())
            {
                //check if the artifact already exists
                var artifact = await dumplingDb.Artifacts.FindAsync(cancelToken, hash);

                //if the file doesn't already exist in the database we need to save it and index it
                if (artifact == null)
                {
                    using (var uploader = new ArtifactUploader())
                    {
                        await UploadArtifactAsync(uploader, dumplingDb, hash, localpath, cancelToken);
                    }
                }
            }
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

        private async Task<Artifact> UploadDumpArtifactAsync(ArtifactUploader uploader, DumplingDb dumplingDb, Dump dump, string hash, string localpath, bool debugCritical, CancellationToken cancelToken)
        {
            var artifact = await UploadArtifactAsync(uploader, dumplingDb, hash, localpath, cancelToken);

            var dumpArtifact = await dumplingDb.DumpArtifacts.FindAsync(dump.DumpId, localpath);

            if (dumpArtifact == null)
            {
                dumpArtifact = new DumpArtifact()
                {
                    DumpId = dump.DumpId,
                    LocalPath = localpath,
                    DebugCritical = debugCritical
                };

                dump.DumpArtifacts.Add(dumpArtifact);
            }

            dumpArtifact.Hash = artifact.Hash;

            if (dumpArtifact.Index == null)
            {
                dumpArtifact.Index = artifact.Indexes.FirstOrDefault()?.Index;
            }

            await dumplingDb.SaveChangesAsync();

            return artifact;
        }

        private async Task<Artifact> UploadArtifactAsync(ArtifactUploader uploader, DumplingDb dumplingDb, string hash, string localpath, CancellationToken cancelToken)
        {
            //check if the artifact already exists
            var artifact = await dumplingDb.Artifacts.FindAsync(cancelToken, hash);

            //if the file doesn't already exist in the database we need to save it and index it
            if (artifact == null)
            {
                artifact = await UploadAndStoreArtifactAsync(uploader, Path.GetFileName(localpath).ToLowerInvariant(), hash, dumplingDb, cancelToken);

                await dumplingDb.SaveChangesAsync();
            }

            return artifact;
        }

        private async Task<Artifact> UploadAndStoreArtifactAsync(ArtifactUploader uploader, string fileName, string expectedHash, DumplingDb dumplingDb, CancellationToken cancelToken)
        {
            using (var requestStream = await Request.Content.ReadAsStreamAsync())
            {
                await uploader.UploadStreamAsync(requestStream, fileName, cancelToken);
            }

            //if the hash doesn't match the expected hash throw
            if (uploader.Hash != expectedHash)
            {
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, "The given hash did not match the SHA1 hash of the uploaded file"));
            }

            var artifact = new Artifact
            {
                Hash = uploader.Hash,
                Uuid = uploader.Uuid,
                Format = uploader.Format,
                FileName = uploader.FileName,
                Size = uploader.Decompressed.Length,
                CompressedSize = uploader.Compressed.Length,
                UploadTime = DateTime.UtcNow
            };

            artifact.Indexes.Add(new ArtifactIndex() { Index = uploader.Index, Hash = uploader.Hash });

            //otherwise create the file entry in the db
            await dumplingDb.AddArtifactAsync(artifact);

            await dumplingDb.SaveChangesAsync();

            await dumplingDb.Entry(artifact).GetDatabaseValuesAsync();

            //upload the artifact to blob storage
            artifact.Url = await DumplingStorageClient.StoreArtifactAsync(uploader.Compressed, uploader.Hash, uploader.CompressedFileName);

            await dumplingDb.SaveChangesAsync();
            return artifact;
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

        private static async Task<string> DecompAndHashAsync(Stream compressed, Stream decompressed, CancellationToken cancelToken)
        {
            string hash = null;

            using (var sha1 = SHA1.Create())
            using (var gzStream = new GZipStream(compressed, CompressionMode.Decompress, true))
            {
                var buff = new byte[BUFF_SIZE];

                int cbyte;

                while ((cbyte = await gzStream.ReadAsync(buff, 0, buff.Length)) > 0 && !cancelToken.IsCancellationRequested)
                {
                    sha1.TransformBlock(buff, 0, cbyte, buff, 0);

                    await decompressed.WriteAsync(buff, 0, cbyte);
                }

                await decompressed.FlushAsync();

                if (!cancelToken.IsCancellationRequested)
                {
                    sha1.TransformFinalBlock(buff, 0, 0);

                    hash = string.Concat(sha1.Hash.Select(b => b.ToString("x2"))).ToLowerInvariant();
                }
            }

            return hash;

        }
        private class DumpArtifactUploader : ArtifactUploader
        {
            private object _fileFormatReader;

            public DumpArtifactUploader()
            {
            }

            public string DumpOS { get; private set; }

            public IList<DumpArtifact> GetLoadedModules(string dumpId)
            {
                switch (this.Format)
                {
                    case "elfcore":
                        return ReadELFCoreLoadedModules(dumpId);
                    default:
                        return null;
                }
            }

            protected override void ComputeFormatAndIndex()
            {
                if (IsELFCore())
                {
                    Format = ArtifactFormat.ElfCore;
                    DumpOS = OS.Linux;
                }
                else if (IsMinidump())
                {
                    Format = ArtifactFormat.Minidump;
                    DumpOS = OS.Windows;
                }
                else if (IsMachCore())
                {
                    Format = ArtifactFormat.MachCore;
                    DumpOS = OS.Mac;
                }
                else
                {
                    Format = ArtifactFormat.Unknown;
                    DumpOS = OS.Unknown;
                }
                Uuid = Hash;
                Index = BuildIndexFromModuleUUID(Hash, IndexPrefix.SHA1, FileName);
            }

            private bool IsELFCore()
            {
                try
                {
                    var coreFile = new ELFCoreFile(new StreamAddressSpace(Decompressed));

                    //get this property so that it will force the validation of the file format before we allocate anything else
                    var fileTable = coreFile.FileTable;

                    _fileFormatReader = coreFile;

                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private bool IsMinidump()
            {
                try
                {
                    return Minidump.IsValidMinidump(new StreamAddressSpace(Decompressed));
                }
                catch
                {
                    return false;
                }
            }

            private bool IsMachCore()
            {
                try
                {
                    var machCore = new MachCore(new StreamAddressSpace(Decompressed));

                    return machCore.IsValidCoreFile;
                }
                catch
                {
                    return false;
                }
            }

            private IList<DumpArtifact> ReadELFCoreLoadedModules(string dumpId)
            {
                try
                {
                    var coreFile = _fileFormatReader as ELFCoreFile;

                    //get this property so that it will force the validation of the file format before we allocate anything else
                    var fileTable = coreFile.FileTable;

                    var dumpArtifacts = new List<DumpArtifact>();

                    foreach (var image in coreFile.LoadedImages)
                    {
                        string index = null;
                        string uuid = null;
                        bool executableImage = false;
                        try
                        {
                            executableImage = image.Image.Header.Type == ELFHeaderType.Executable;

                            //this call will throw an exception if the loaded image doesn't have a build id.  
                            //Unfortunately there is no way to check if build id exists without ex
                            var buildId = image.Image.BuildID;

                            if (buildId != null)
                            {
                                uuid = string.Concat(buildId.Select(b => b.ToString("x2"))).ToLowerInvariant();

                                index = BuildIndexFromModuleUUID(uuid, IndexPrefix.Elf, Path.GetFileName(image.Path));
                            }
                        }
                        catch { }

                        dumpArtifacts.Add(new DumpArtifact() { DumpId = dumpId, LocalPath = image.Path, Index = index, DebugCritical = true, ExecutableImage = executableImage });

                        //if the image is libcoreclr.so also add libmscordaccore.so and libsos.so at the same path
                        if(Path.GetFileName(image.Path) == "libcoreclr.so")
                        {
                            var localDir = Path.GetDirectoryName(image.Path);

                            //currently the dac index and the sos index are not imbedded in libcoreclr.so 
                            //this should eventually be the case, but for now set the indexes using the buildid from libcoreclr.so
                            //and we will manually add these indexes to the atifact store
                            dumpArtifacts.Add(new DumpArtifact() { DumpId = dumpId, LocalPath = Path.Combine(localDir, "libmscordaccore.so").Replace('\\', '/'), Index = BuildIndexFromModuleUUID(uuid, IndexPrefix.Elf, "libmscordaccore.so"), DebugCritical = true });

                            dumpArtifacts.Add(new DumpArtifact() { DumpId = dumpId, LocalPath = Path.Combine(localDir, "libsos.so").Replace('\\', '/'), Index = BuildIndexFromModuleUUID(uuid, IndexPrefix.Elf, "libsos.so"), DebugCritical = true });
                        }
                    }

                    return dumpArtifacts;
                }
                catch
                {
                    return null;
                }
            }
            

        }

        private class ArtifactUploader : IDisposable
        {
            public Stream Compressed { get; protected set; }

            public Stream Decompressed { get; protected set; }

            public string Hash { get; protected set; }

            public string Uuid { get; protected set; }

            public string Index { get; protected set; }

            public string Format { get; protected set; }

            public string FileName { get; protected set; }

            public string CompressedFileName { get { return FileName + ".gz"; } }

            public async Task UploadStreamAsync(Stream stream, string filename, CancellationToken cancelToken)
            {
                FileName = filename.ToLowerInvariant();
                
                Compressed = CreateTempFile();

                await stream.CopyToAsync(Compressed, BUFF_SIZE, cancelToken);

                await Compressed.FlushAsync();

                Compressed.Position = 0;

                Decompressed = CreateTempFile();

                await DecompAndHashAsync(cancelToken);

                Compressed.Position = 0;

                if(!cancelToken.IsCancellationRequested)
                {
                    ComputeFormatAndIndex();
                }

                Decompressed.Position = 0;

                Compressed.Position = 0;
            }
            
            public void Dispose()
            {
                if (this.Compressed != null)
                {
                    Compressed.Close();
                    Compressed.Dispose();
                }

                if(this.Decompressed != null)
                {
                    Decompressed.Close();
                    Decompressed.Dispose();
                }
            }

            protected virtual void ComputeFormatAndIndex()
            {
                string uuid;

                string indexPrefix;

                if(TryGetElfIndex(Decompressed, out uuid))
                {
                    Format = ArtifactFormat.Elf;

                    indexPrefix = IndexPrefix.Elf;
                }
                else if(TryGetPEIndex(Decompressed, out uuid))
                {
                    Format = ArtifactFormat.PE;

                    indexPrefix = IndexPrefix.PE;
                }
                else if (TryGetPDBIndex(Decompressed, out uuid))
                {
                    Format = ArtifactFormat.PDB;

                    indexPrefix = IndexPrefix.PDB;
                }
                else
                {
                    Format = ArtifactFormat.Unknown;

                    uuid = Hash;

                    indexPrefix = IndexPrefix.SHA1;
                }

                Uuid = uuid;

                Index = BuildIndexFromModuleUUID(uuid, indexPrefix, FileName);
            }


            protected static string BuildIndexFromModuleUUID(string uuid, string indexPrefix, string filename)
            {
                if (string.IsNullOrEmpty(uuid))
                {
                    return null;
                }
                
                var key = new StringBuilder();

                key.Append(filename);

                key.Append("/");

                key.Append(indexPrefix);
                
                key.Append(uuid);

                key.Append("/");

                key.Append(filename);

                key.Append(".gz");

                return key.ToString();
            }

            private static bool TryGetElfIndex(Stream stream, out string uuid)
            {
                uuid = null;

                try
                {
                    var elf = new ELFFile(new StreamAddressSpace(stream));

                    if (!elf.Ident.IsIdentMagicValid.Check())
                    {
                        return false;
                    }

                    if (elf.BuildID == null || elf.BuildID.Length != 20)
                    {
                        return false;
                    }
                    
                    uuid = string.Concat(elf.BuildID.Select(b => b.ToString("x2"))).ToLowerInvariant();

                    return true;
                }
                catch (InputParsingException)
                {
                    return false;
                }

            }
            
            private static bool TryGetPEIndex(Stream stream, out string uuid)
            {
                uuid = null;

                StreamAddressSpace fileAccess = new StreamAddressSpace(stream);
                try
                {
                    PEFile reader = new PEFile(fileAccess);
                    if (!reader.HasValidDosSignature.Check())
                    {
                        return false;
                    }

                    uuid = reader.Timestamp.ToString("x").ToLowerInvariant() + reader.SizeOfImage.ToString("x").ToLowerInvariant();
                    
                    return true;
                }
                catch (InputParsingException)
                {
                    return false;
                }
            }

            private static bool TryGetPDBIndex(Stream stream, out string uuid)
            {
                uuid = null;
                try
                {
                    PDBFile pdb = new PDBFile(new StreamAddressSpace(stream));

                    if (!pdb.Header.IsMagicValid.Check())
                    {
                        return false;
                    }
    
                    uuid = pdb.Signature.ToString().Replace("-", "").ToLowerInvariant() + pdb.Age.ToString("x").ToLowerInvariant();
                    
                    return true;
                }
                catch (InputParsingException)
                {
                    return false;
                }
            }

            private async Task DecompAndHashAsync(CancellationToken cancelToken)
            {
                this.Hash = await DumplingApiController.DecompAndHashAsync(this.Compressed, this.Decompressed, cancelToken);
            }
        }

        private class TempDirectory : IDisposable
        {
            private string _basepath;

            public TempDirectory()
            {
                string root = HttpContext.Current.Server.MapPath("~/App_Data");

                string _basepath = Path.Combine(root, Path.GetTempFileName());

                Directory.CreateDirectory(_basepath);
            }

            public string BasePath
            {
                get { return _basepath; }
            }

            public Stream CreateTempFile(string relativePath)
            {
                return File.Create(Path.Combine(_basepath, relativePath), BUFF_SIZE, FileOptions.Asynchronous | FileOptions.RandomAccess);
            }

            public void Dispose()
            {
                NukeDir(_basepath);
            }
            
            private void NukeDir(string path)
            {
                foreach (var dirpath in Directory.EnumerateDirectories(path))
                {
                    NukeDir(dirpath);
                }

                foreach (var filepath in Directory.EnumerateFiles(path))
                {
                    File.Delete(filepath);
                }

                Directory.Delete(path);
            }
        }
    }
}
