using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.IO.Compression;
using dumpling.db;
using System.Text;
using FileFormats;
using FileFormats.ELF;
using FileFormats.PE;
using FileFormats.PDB;

namespace dumpling.web.Storage
{
    public class ArtifactProcessor
    {
        protected string _path;
        protected DumplingDb _dumplingDb;
        protected string _localRoot;
        

        public ArtifactProcessor(string localRoot, string path, string expectedHash, string dumpId = null, string localPath = null, bool debugCritical = false )
        {
            _localRoot = localRoot;
            _path = path;
            ExpectedHash = expectedHash;
            DumpId = dumpId;
            LocalPath = localPath;
            DebugCritical = debugCritical;

        }

        public string Hash { get; protected set; }

        public string ExpectedHash { get; protected set; }

        public string Uuid { get; protected set; }

        public string Index { get; protected set; }

        public string Format { get; protected set; }

        public string FileName { get; protected set; }

        public string LocalPath { get; protected set; }

        public string CompressedFileName { get { return FileName + ".gz"; } }

        public long Size { get; protected set; }

        public long CompressedSize { get; protected set; }

        public bool DebugCritical { get; protected set; }

        public string DumpId { get; protected set; }

        public async Task ProcessAsync()
        {
            try
            {
                using (_dumplingDb = new DumplingDb())
                {
                    FileName = Path.GetFileName(_path).ToLowerInvariant();

                    using (Stream decompressed = await DecompAndHashAsync())
                    {
                        if (Hash != ExpectedHash)
                        {
                            throw new ArgumentException("The given hash did not match the SHA1 hash of the uploaded file.");
                        }

                        Size = decompressed.Length;

                        ProcessDecompressedFile(decompressed);
                    }

                    await StoreArtifactAsync();

                    File.Delete(_path);
                }
            }
            catch (Exception e)
            {
                //need to do some loggin here
                throw e;
            }
        }

        protected virtual async Task StoreArtifactAsync()
        {
            var artifact = new Artifact
            {
                Hash = Hash,
                Uuid = Uuid,
                Format = Format,
                FileName = FileName,
                Size = Size,
                CompressedSize = CompressedSize,
                UploadTime = DateTime.UtcNow
            };

            if (Index != null)
            {
                artifact.Indexes.Add(new ArtifactIndex() { Index = Index, Hash = Hash });
            }

            DumpArtifact dumpArtifact = null;


            if (DumpId != null)
            {
                dumpArtifact = await _dumplingDb.DumpArtifacts.FindAsync(DumpId, LocalPath);

                if (dumpArtifact == null)
                {
                    dumpArtifact = new DumpArtifact()
                    {
                        DumpId = DumpId,
                        LocalPath = LocalPath,
                        DebugCritical = DebugCritical
                    };
                    
                }

                if (dumpArtifact.Index == null)
                {
                    dumpArtifact.Index = artifact.Indexes.FirstOrDefault()?.Index;
                }
            }

            //otherwise create the file entry in the db
            await _dumplingDb.AddArtifactAsync(artifact);

            await _dumplingDb.SaveChangesAsync();

            await _dumplingDb.Entry(artifact).GetDatabaseValuesAsync();

            using (var compressed = File.OpenRead(_path))
            {
                //upload the artifact to blob storage
                artifact.Url = await DumplingStorageClient.StoreArtifactAsync(compressed, Hash, CompressedFileName);
            }

            if(dumpArtifact != null && dumpArtifact.Hash != artifact.Hash)
            {
                dumpArtifact.Hash = artifact.Hash;
            }

            await _dumplingDb.SaveChangesAsync();

        }

        protected virtual void ProcessDecompressedFile(Stream decompressed)
        {
            string uuid;

            string indexPrefix;

            if (TryGetElfIndex(decompressed, out uuid))
            {
                Format = ArtifactFormat.Elf;

                indexPrefix = IndexPrefix.Elf;
            }
            else if (TryGetPEIndex(decompressed, out uuid))
            {
                Format = ArtifactFormat.PE;

                indexPrefix = IndexPrefix.PE;
            }
            else if (TryGetPDBIndex(decompressed, out uuid))
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

        private async Task<Stream> DecompAndHashAsync()
        {
            var decompressed = CreateTempFile();

            using (var compressed = File.OpenRead(_path))
            {
                CompressedSize = compressed.Length;

                Hash = await DecompAndHashAsync(compressed, decompressed);
            }

            decompressed.Position = 0;

            return decompressed;
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

        private const int BUFF_SIZE = 1024 * 8;

        private static async Task<string> DecompAndHashAsync(Stream compressed, Stream decompressed)
        {
            string hash = null;

            using (var sha1 = SHA1.Create())
            using (var gzStream = new GZipStream(compressed, CompressionMode.Decompress, true))
            {
                var buff = new byte[BUFF_SIZE];

                int cbyte;

                while ((cbyte = await gzStream.ReadAsync(buff, 0, buff.Length)) > 0)
                {
                    sha1.TransformBlock(buff, 0, cbyte, buff, 0);

                    await decompressed.WriteAsync(buff, 0, cbyte);
                }

                await decompressed.FlushAsync();

                sha1.TransformFinalBlock(buff, 0, 0);

                hash = string.Concat(sha1.Hash.Select(b => b.ToString("x2"))).ToLowerInvariant();

            }

            return hash;

        }

        private Stream CreateTempFile(string path = null)
        {
            path = path ?? Path.GetTempFileName();


            string tempPath = Path.Combine(_localRoot, path);

            if (!Directory.Exists(Path.GetDirectoryName(tempPath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath));
            }

            //this file is not disposed of here b/c it is deleted on close
            //callers of this method are responsible for disposing the file
            return File.Create(tempPath, BUFF_SIZE, FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        }
    }
}