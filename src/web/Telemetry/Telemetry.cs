using Microsoft.ApplicationInsights;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace dumpling.web.telemetry
{
    public static class Telemetry
    {
        public static TelemetryClient Client { get; } = new TelemetryClient();

        public static bool TrackExceptionFilter(Exception e)
        {
            Client.TrackException(e);

            return false;
        }
    }
}