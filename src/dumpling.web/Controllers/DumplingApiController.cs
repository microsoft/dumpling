using dumpling.db;
using dumpling.web.Storage;
using FileFormats;
using FileFormats.ELF;
using System;
using System.Collections.Generic;
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


        [Route("api/client/scripts")]
        [HttpGet]
        public HttpResponseMessage GetClientTools([FromUri] string filename)
        {
            string path = HttpContext.Current.Server.MapPath("~/Content/client/" + filename);

            HttpResponseMessage result = new HttpResponseMessage(HttpStatusCode.OK);
            var stream = new FileStream(path, FileMode.Open);
            result.Content = new StreamContent(stream);
            result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            result.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = filename };
            return result;

        }

        [Route("api/dumplings/{dumplingId:int}")]
        [HttpGet]
        public async Task<IEnumerable<DumpArtifact>> GetDumplingArtifacts(int dumplingId)
        {
            using (DumplingDb dumplingDb = new DumplingDb())
            {
                var dump = await dumplingDb.Dumps.FindAsync(dumplingId);

                return dump == null ? new DumpArtifact[] { } : dump.DumpArtifacts.ToArray();
            }
        }

        [Route("api/dumplings/create")]
        [HttpGet]
        public async Task<int> CreateDumpling([FromUri] string origin, [FromUri] string displayName)
        {
            using (DumplingDb dumplingDb = new DumplingDb())
            {
                var dump = new Dump() { Origin = origin, DisplayName = displayName, DumpTime = DateTime.UtcNow };

                dumplingDb.Dumps.Add(dump);

                await dumplingDb.SaveChangesAsync();

                return dump.DumpId;
            }
        }

        [Route("api/dumplings/uploads/{dumplingId:int}")]
        [HttpPost]
        public async Task<IEnumerable<DumpArtifact>> UploadDumpFile(int dumplingId)
        {
            throw new NotImplementedException();
        }

        [Route("api/artifacts/uploads/{hash}")]
        [HttpPost]
        public async Task UploadArtifact(string hash, [FromUri] string localpath, [FromUri] int? dumplingId = null, CancellationToken cancelToken = default(CancellationToken))
        {
            using (DumplingDb dumplingDb = new DumplingDb())
            {
                Dump dumpling = null;

                //if the dumplingId is not null find the dump
                if(dumplingId.HasValue)
                {
                    dumpling = await dumplingDb.Dumps.FindAsync(cancelToken, dumplingId.Value);

                    //if the specified dump was not found throw an exception 
                    if(dumpling == null)
                    {
                        throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, "The given dumplingId is invalid"));
                    }
                }

                //check if the artifact already exists
                var artifact = await dumplingDb.Artifacts.FindAsync(cancelToken, hash);

                //if the file doesn't already exist in the database we need to save it and index it
                if (artifact == null)
                {
                    await UploadAndStoreArtifactAsync(Path.GetFileName(localpath).ToLowerInvariant(), hash, dumplingDb, cancelToken);

                    //find all DumpArtifacts with the specified index and update their Hash
                    foreach (var d in dumplingDb.DumpArtifacts.Where(da => da.Index == artifact.Index && da.Hash == null))
                    {
                        d.Hash = artifact.Hash;
                    }

                }


                //if the client specified a dumpling id dumpArtifact for that dump as well
                if (dumpling != null)
                {
                    var dumpArtifact = new DumpArtifact()
                    {
                        Hash = artifact.Hash,
                        Index = artifact.Index,
                        LocalPath = localpath
                    };

                    dumpling.DumpArtifacts.Add(dumpArtifact);
                }

                //update the database
                await dumplingDb.SaveChangesAsync();
            }
        }

        [Route("api/artifacts/downloads/{*index}")]
        public async Task<IHttpActionResult> GetArtifact(string index)
        {
            throw new NotImplementedException();
        }
        
        private async Task<Artifact> UploadAndStoreArtifactAsync(string fileName, string expectedHash, DumplingDb dumplingDb, CancellationToken cancelToken)
        {
            using (var artifactUpload = new ArtifactUploader())
            {
                using (var requestStream = await Request.Content.ReadAsStreamAsync())
                {
                    await artifactUpload.UploadStreamAsync(requestStream, fileName, cancelToken);
                }

                //if the hash doesn't match the expected hash throw
                if (artifactUpload.Hash != expectedHash)
                {
                    throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, "The given hash did not match the SHA1 hash of the uploaded file"));
                }

                var artifact = new Artifact
                {
                    Hash = artifactUpload.Hash,
                    Index = artifactUpload.Index,
                    Format = artifactUpload.Format,
                    UploadTime = DateTime.UtcNow
                };

                //otherwise create the file entry in the db
                dumplingDb.Artifacts.Add(artifact);

                //upload the artifact to blob storage
                await DumplingStorageClient.StoreArtifactAsync(artifactUpload.Compressed, artifactUpload.Hash);

                return artifact;
            }
        }
        
        private class ArtifactUploader : IDisposable
        {
            public Stream Compressed { get; private set; }

            public Stream Decompressed { get; private set; }
            
            public string Hash { get; private set; }

            public string Index { get; private set; }

            public string Format { get; private set; }

            public string FileName { get; private set; }

            public async Task UploadStreamAsync(Stream stream, string filename, CancellationToken cancelToken)
            {
                FileName = filename.ToLowerInvariant();

                Compressed = CreateTempFile();

                await stream.CopyToAsync(Compressed, BUFF_SIZE, cancelToken);

                Compressed.Position = 0;

                Decompressed = CreateTempFile();

                await DecompAndHashAsync(cancelToken);

                if(!cancelToken.IsCancellationRequested)
                {
                    ComputeFormatAndIndex();
                }
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

            private void ComputeFormatAndIndex()
            {
                string index;
                if(TryGetElfIndex(Decompressed, FileName, out index))
                {
                    Format = "elf";
                }
                else
                {
                    Format = "unknown";

                    index = GetSha1Index(FileName, Hash);
                }

                Index = index;
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

                    var key = new StringBuilder();
                    key.Append(filename);
                    key.Append("/elf-buildid-");
                    key.Append(string.Concat(elf.BuildID.Select(b => b.ToString("x2"))).ToLowerInvariant());
                    key.Append("/");
                    key.Append(filename);
                    index = key.ToString();

                    return true;
                }
                catch (InputParsingException)
                {
                    return false;
                }

            }

            private static string GetSha1Index(string filename, string hash)
            {
                StringBuilder index = new StringBuilder();

                index.Append(filename);
                index.Append("/");
                index.Append("sha1-");
                index.Append(hash);
                index.Append("/");
                index.Append(filename);
                return index.ToString();
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
