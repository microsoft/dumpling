using dumpling.db;
using dumpling.web.Storage;
using FileFormats;
using FileFormats.ELF;
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
        private const int BUFF_SIZE = 1024 * 8;

        public bool AllowDumplicateDumps = true;

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

        [Route("api/dumplings/{dumplingId}/artifacts")]
        [HttpGet]
        public async Task<IEnumerable<DumpArtifact>> GetDumplingArtifacts(string dumplingId)
        {
            using (DumplingDb dumplingDb = new DumplingDb())
            {
                var dump = await dumplingDb.Dumps.FindAsync(dumplingId);

                return dump == null ? new DumpArtifact[] { } : dump.DumpArtifacts.ToArray();
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
                foreach(var existingProp in dumpling.Properties)
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
                foreach(var newProp in propDict)
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

                dumpling = new Dump() { DumpId = hash, Origin = origin, DisplayName = displayName, DumpTime = DateTime.UtcNow };

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

                    foreach (var dumpArtifact in uploader.GetLoadedModules(dumpling.DumpId))
                    {
                        var artifactIndex = await dumplingDb.ArtifactIndexes.FindAsync(dumpArtifact.Index);

                        if(artifactIndex != null)
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

        private async Task<HttpResponseMessage> GetArtifactRedirectAsync(Artifact artifact, CancellationToken cancelToken)
        {
            if (artifact == null)
            {
                return Request.CreateResponse(HttpStatusCode.NotFound);
            }

            var stream = new MemoryStream();

            var blob = DumplingStorageClient.BlobClient.GetBlobReferenceFromServer(new Uri(artifact.Url));

            var sasConstraints = new SharedAccessBlobPolicy()
            {
                SharedAccessStartTime = DateTime.UtcNow.AddMinutes(-1),
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(1),
                Permissions = SharedAccessBlobPermissions.Read
            };

            var blobToken = blob.GetSharedAccessSignature(sasConstraints);

            var tempAccessUrl = artifact.Url + blobToken;

            var httpResponse = await ((IHttpActionResult)this.Redirect(tempAccessUrl)).ExecuteAsync(cancelToken);

            httpResponse.Headers.Add("dumpling-filename", artifact.FileName);

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
                Format = uploader.Format,
                FileName = uploader.FileName,
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
        
        private class DumpArtifactUploader : ArtifactUploader
        {
            private object _fileFormatReader;

            public DumpArtifactUploader()
            {
            }

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
                    Format = "elfcore";
                }
                
                Index = BuildIndexFromModuleUUID(Hash, SHA1_INDEXSTYLE_ID, FileName);
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

                        try
                        {
                            //this call will throw an exception if the loaded image doesn't have a build id.  
                            //Unfortunately there is no way to check if build id exists without ex
                            var buildId = image.Image.BuildID;

                            if (buildId != null)
                            {
                                index = BuildIndexFromModuleUUID(buildId, ELF_INDEXSTYLE_ID, Path.GetFileName(image.Path));
                            }
                        }
                        catch { }

                        dumpArtifacts.Add(new DumpArtifact() { DumpId = dumpId, LocalPath = image.Path, Index = index, DebugCritical = true });
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
                string index;
                if(TryGetElfIndex(Decompressed, FileName, out index))
                {
                    Format = "elf";
                }
                else if(TryGetPEIndex(Decompressed, FileName, out index))
                {
                    Format = "pe";
                }
                else
                {
                    Format = "unknown";

                    index = BuildIndexFromModuleUUID(Hash, SHA1_INDEXSTYLE_ID, FileName);
                }

                Index = index;
            }

            protected const string SHA1_INDEXSTYLE_ID = "sha1-";
            protected const string ELF_INDEXSTYLE_ID = "elf-buildid-";
            protected const string PE_INDEXSTYLE_ID = "";
            protected const string PDB_INDEXSTYLE_ID = "";

            protected static string BuildIndexFromModuleUUID(byte[] uuid, string indexStyleId, string filename)
            {
                string uuidStr = string.Concat(uuid.Select(b => b.ToString("x2"))).ToLowerInvariant();

                return BuildIndexFromModuleUUID(uuidStr, indexStyleId, filename);
            }

            protected static string BuildIndexFromModuleUUID(string uuid, string indexStyleId, string filename)
            {
                var key = new StringBuilder();

                key.Append(filename);

                key.Append("/");

                key.Append(indexStyleId);
                
                key.Append(uuid);

                key.Append("/");

                key.Append(filename);

                key.Append(".gz");

                return key.ToString();
            }

            private static bool TryGetElfIndex(Stream stream, string filename, out string index)
            {
                index = null;

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

                    index = BuildIndexFromModuleUUID(elf.BuildID, ELF_INDEXSTYLE_ID, filename);

                    return true;
                }
                catch (InputParsingException)
                {
                    return false;
                }

            }
            
            private static bool TryGetPEIndex(Stream stream, string filename, out string index)
            {
                index = null;
                string extension = Path.GetExtension(filename);
                if (extension != ".dll" && extension != ".exe")
                {
                    return false;
                }

                StreamAddressSpace fileAccess = new StreamAddressSpace(stream);
                try
                {
                    PEFile reader = new PEFile(fileAccess);
                    if (!reader.HasValidDosSignature.Check())
                    {
                        return false;
                    }

                    string key = reader.Timestamp.ToString("x").ToLowerInvariant() + reader.SizeOfImage.ToString("x").ToLowerInvariant();

                    index = BuildIndexFromModuleUUID(key, PE_INDEXSTYLE_ID, filename);

                    return true;
                }
                catch (InputParsingException)
                {
                    return false;
                }
            }

            private static bool TryGetPDBIndex(Stream stream, string filename, out string index)
            {
                index = null;
                try
                {
                    if (Path.GetExtension(filename) != ".pdb")
                    {
                        return false;
                    }
                    PDBFile pdb = new PDBFile(new StreamAddressSpace(stream));
                    if (!pdb.Header.IsMagicValid.Check())
                    {
                        return false;
                    }
    
                    string key = pdb.Signature.ToString().Replace("-", "").ToLowerInvariant() + pdb.Age.ToString("x").ToLowerInvariant();
                    index = BuildIndexFromModuleUUID(key, PDB_INDEXSTYLE_ID, filename);
                    return true;
                }
                catch (InputParsingException)
                {
                    return false;
                }
            }

            private async Task DecompAndHashAsync(CancellationToken cancelToken)
            {
                using (var sha1 = SHA1.Create())
                using (var gzStream = new GZipStream(Compressed, CompressionMode.Decompress, true))
                {
                    var buff = new byte[BUFF_SIZE];

                    int cbyte;

                    while ((cbyte = await gzStream.ReadAsync(buff, 0, buff.Length)) > 0 && !cancelToken.IsCancellationRequested)
                    {
                        sha1.TransformBlock(buff, 0, cbyte, buff, 0);

                        await Decompressed.WriteAsync(buff, 0, cbyte);
                    }

                    await Decompressed.FlushAsync();

                    if (!cancelToken.IsCancellationRequested)
                    {
                        sha1.TransformFinalBlock(buff, 0, 0);

                        Hash = string.Concat(sha1.Hash.Select(b => b.ToString("x2"))).ToLowerInvariant();
                    }
                }
                
            }
            
            private Stream CreateTempFile()
            {
                string root = HttpContext.Current.Server.MapPath("~/App_Data");

                string tempPath = Path.Combine(root, Path.GetTempFileName());

                //this file is not disposed of here b/c it is deleted on close
                //callers of this method are responsible for disposing the file
                return File.Create(tempPath, BUFF_SIZE, FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.RandomAccess);
            }
        }
    }
}
