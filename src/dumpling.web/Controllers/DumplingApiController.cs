using dumpling.db;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;

namespace dumpling.web.Controllers
{
    public class DumplingApiController : ApiController
    {
        private const int BUFF_SIZE = 1024 * 8;

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
        public async Task UploadArtifact(string hash, [FromUri] int? dumplingId = null, [FromUri] string localpath = null, CancellationToken cancelToken = default(CancellationToken))
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
        
        private async Task<Artifact> UploadArtifactAsync(string expectedHash, DumplingDb dumplingDb, CancellationToken cancelToken)
        {
            //upload the content to a temp file, this temp file will be deleted when the file is closed
            using (var compressed = await UploadContentToTempFileAsync(cancelToken))
            {
                //decompress the file and 
                using (var decompressed = CreateTempFile())
                {
                    var hash = await ComputeHashAndDecompressAsync(compressed, decompressed);

                    //if the hash doesn't match the expected hash throw
                    if (hash != expectedHash)
                    {
                        throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, "The given hash did not match the SHA1 hash of the uploaded file"));
                    }

                    var artifact = new Artifact() { Hash = hash, UploadTime = DateTime.UtcNow};

                    //upload the compressed file to blob storage
                    compressed.Position = 0;

                    //calculate the symstore index of the file

                    return artifact;
                }
            }
        }

        private async Task<Stream> UploadContentToTempFileAsync(CancellationToken cancelToken)
        {
            //get the file from the content
            using (var requestStream = await Request.Content.ReadAsStreamAsync())
            {
                //this file is not disposed of here b/c it is deleted on close
                //callers of this method are responsible for disposing the file
                var tempFileStream = CreateTempFile();
                
                await requestStream.CopyToAsync(tempFileStream, BUFF_SIZE, cancelToken);

                //reset the position of the filestream to zero
                tempFileStream.Position = 0;

                return tempFileStream;
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

        private async Task<string> ComputeHashAndDecompressAsync(Stream compressed, Stream decompresed)
        {
            using (var gzStream = new GZipStream(compressed, CompressionMode.Decompress, true))
            {
                return await ComputeHashAndCopyAsync(gzStream, decompresed);
            }     
        }

        private async Task<string> ComputeHashAndCopyAsync(Stream inStream, Stream outStream)
        {
            var sha1 = SHA1.Create();

            var buff = new byte[BUFF_SIZE];

            int cbyte;

            while((cbyte = await inStream.ReadAsync(buff, 0, buff.Length)) > 0)
            {
                sha1.TransformBlock(buff, 0, cbyte, buff, 0);

                await outStream.WriteAsync(buff, 0, cbyte);
            }
            sha1.TransformFinalBlock(buff, 0, 0);

            return BitConverter.ToString(sha1.Hash).ToLowerInvariant().Replace("-", string.Empty);
        }
    }
}
