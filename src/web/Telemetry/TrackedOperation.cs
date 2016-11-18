using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Web;

namespace dumpling.web.telemetry
{
    public class TrackedOperation : IDisposable
    {
        private Stopwatch _stopwatch;
        private string _opName;
        private Dictionary<string, string> _properties;
        private Dictionary<string, double> _metrics;

        public TrackedOperation(string opName, Dictionary<string, string> properties = null, Dictionary<string, double> metrics = null)
        {
            _stopwatch = new Stopwatch();
            _properties = properties;
            _metrics = metrics;
            _opName = opName;

            Telemetry.Client.TrackEvent(_opName + "_Started", properties: _properties, metrics: _metrics);

            _stopwatch.Start();
        }

        public void Dispose()
        {
            _stopwatch.Stop();

            double duration = Convert.ToDouble(_stopwatch.ElapsedMilliseconds);

            _metrics = _metrics ?? new Dictionary<string, double>();

            _metrics[_opName + "_Instance_Duration"] = duration;

            Telemetry.Client.TrackEvent(_opName + "_Completed", properties: _properties, metrics: _metrics);

            Telemetry.Client.TrackMetric(_opName + "_Duration", duration, _properties);
            

        }
    }
}