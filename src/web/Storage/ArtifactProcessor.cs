using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.IO.Compression;

namespace dumpling.web.Storage
{
    public class ArtifactProcessor
    {
        private static string _path;
        
        public string Hash { get; protected set; }

        public string Uuid { get; protected set; }

        public string Index { get; protected set; }

        public string Format { get; protected set; }

        public string FileName { get; protected set; }

        public string CompressedFileName { get { return FileName + ".gz"; } }

        public virtual async Task Process(string path)
        {
            _path = path;

            FileName = Path.GetFileName(path).ToLowerInvariant();

            //the file is expected to be stored in a parent directory with the expected SHA1 hash as the name
            var expectedHash = Path.GetFileName(Path.GetDirectoryName(path));


        }

        protected virtual void ComputeFormatAndIndex(Stream decompressed)
        {

        } 

        protected async Task<Stream> DecompAndHash()
        {
            using (var compstream = File.OpenRead(_path))
            {

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

    }
}