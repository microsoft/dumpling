using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using dumpling.db;
using Newtonsoft.Json;

namespace dumpling.web.Controllers
{
    public class HomeController : Controller
    {
        public async Task<ActionResult> Index(int? days)
        {
            days = days.HasValue ? days : 10;

            using (var context = new DumplingDb())
            {
                var minTime = DateTime.UtcNow.AddDays(days.Value * -1);

                var dumpsInTimeframe = days == 0 ? context.Dumps : context.Dumps.Where(d => d.DumpTime > minTime);

                var dumpsByFailure = dumpsInTimeframe.GroupBy(d => d.FailureHash);

                var failures = new List<string>();

                foreach (var failure in dumpsByFailure)
                {
                    failures.Add(failure.Key);
                       
                    var dumps = failure.ToArray();

                    ViewData[failure.Key] = dumps;

                    foreach (var dump in dumps)
                    {
                        ViewData["dumpid." + dump.DumpId.ToString()] = dump.GetPropertyBag();
                    }
                }

                ViewBag.Title = "dumpling";

                ViewData["Failures"] = failures.ToArray();
            }
            return View();
        }
    }
}