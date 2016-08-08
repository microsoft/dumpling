using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;

namespace dumpling.web.Controllers
{
    public class DumplingApiController : ApiController
    {

        public async Task GetDumplingManifest(int dumplingId)
        {
            throw new NotImplementedException();
        }

        public async Task<int> CreateDumpling(string origin, string displayName)
        {
            throw new NotImplementedException();
        }

        public async Task UploadDumpFile(int dumplingId)
        {
            throw new NotImplementedException();
        }

        public async Task UploadArtifact(int? dumplingId, string index = null, string format = null, string localpath = null)
        {
            throw new NotImplementedException();
        }
    }
}
