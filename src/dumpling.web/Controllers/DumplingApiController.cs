using dumpling.db;
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
        [Route("api/dumplings/{dumplingId:int}")]
        public async Task<IHttpActionResult> GetDumplingManifest(int dumplingId)
        {
            using (DumplingDb dumplingDb = new DumplingDb())
        }

        [Route("api/dumplings/create")]
        [HttpGet]
        public async Task<int> CreateDumpling([FromUri] string origin, [FromUri] string displayName)
        {
            throw new NotImplementedException();
        }

        [Route("api/dumplings/upload/{dumplingId:int}")]
        [HttpPost]
        public async Task UploadDumpFile(int dumplingId)
        {
            throw new NotImplementedException();
        }

        [Route("api/artifacts/upload/{hash}")]
        [HttpPost]
        public async Task UploadArtifact(string hash, [FromUri] int? dumplingId, [FromUri] string index = null, [FromUri] string format = null, [FromUri] string localpath = null)
        {
            throw new NotImplementedException();
        }

        [Route("api/artifacts/download/{*index}")]
        public async Task<IHttpActionResult> GetArtifact(string index)
        {
            throw new NotImplementedException();
        }
    }
}
