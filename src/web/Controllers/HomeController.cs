using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using dumpling.db;
using Newtonsoft.Json;
using System.Data.Entity;

namespace dumpling.web.Controllers
{
    public class HomeController : Controller
    {
        [Route("{days:int?}")]
        public async Task<ActionResult> Index(int? days)
        {
            return await this.FilterIndex(null, null);
        }

        [Route("{days:int?}/filter{*queryParams}")]
        public async Task<ActionResult> FilterIndex(int? days, string queryParams)
        {
            var queryDict = new Dictionary<string, string>();

            foreach(var key in this.Request.QueryString.AllKeys)
            {
                queryDict[key] = this.Request.QueryString.Get(key);
            }

            days = days.HasValue ? days : 10;

            var minTime = DateTime.UtcNow.AddDays(days.Value * -1);
            var maxTime = DateTime.UtcNow.AddDays(1);

            List<Dump> loadedDumps = null;

            using (var dumplingDb = new DumplingDb())
            {                
                loadedDumps = dumplingDb.FindDumps(minTime, maxTime, queryDict).ToList();

                foreach(var dump in loadedDumps)
                {
                    dumplingDb.Entry(dump).Reference(d => d.Failure).Load();
                    dumplingDb.Entry(dump).Collection(d => d.Properties).Load();
                }
            }

            var dumpsByFailure = loadedDumps.GroupBy(d => d.FailureHash);

            var failures = new List<string>();

            foreach (var failure in dumpsByFailure)
            {
                failures.Add(failure.Key ?? "UNTRIAGED");

                var dumps = failure.ToArray();

                ViewData[failure.Key ?? "UNTRIAGED"] = dumps;

                foreach (var dump in dumps)
                {
                    ViewData["dumpid." + dump.DumpId] = JsonConvert.SerializeObject(dump.GetPropertyBag());
                }
            }

            ViewBag.Title = "dumpling";

            ViewData["Failures"] = failures.ToArray();
            return View();
        }
    }
}