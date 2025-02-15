using System;
using System.Collections.Generic;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Serilog.Events;
using Serilog.Sinks.ApplicationInsights.TelemetryConverters;

namespace Wino.Services.Misc
{
    internal class WinoTelemetryConverter : EventTelemetryConverter
    {
        private readonly string _userDiagnosticId;

        public WinoTelemetryConverter(string userDiagnosticId)
        {
            _userDiagnosticId = userDiagnosticId;
        }

        public override IEnumerable<ITelemetry> Convert(LogEvent logEvent, IFormatProvider formatProvider)
        {
            foreach (ITelemetry telemetry in base.Convert(logEvent, formatProvider))
            {
                // Assign diagnostic id as user id.
                telemetry.Context.User.Id = _userDiagnosticId;

                yield return telemetry;
            }
        }

        public override void ForwardPropertiesToTelemetryProperties(LogEvent logEvent, ISupportProperties telemetryProperties, IFormatProvider formatProvider)
        {
            ForwardPropertiesToTelemetryProperties(logEvent, telemetryProperties, formatProvider,
                includeLogLevel: true,
                includeRenderedMessage: true,
                includeMessageTemplate: false);
        }
    }
}
