using System;
using System.Linq;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.Connectivity;

/// <summary>
/// Contains validation of the CalDav server connectivity during account setup.
/// Crosses the RPC pipe; the constructor must stay public for the STJ source generator.
/// </summary>
public class CalDavConnectivityTestResults
{
    public CalDavConnectivityTestResults() { }

    public bool IsSuccess { get; set; }

    public string FailedReason { get; set; }

    public static CalDavConnectivityTestResults Success() => new CalDavConnectivityTestResults() { IsSuccess = true };

    public static CalDavConnectivityTestResults Failure(Exception ex) => new CalDavConnectivityTestResults()
    {
        FailedReason = string.Join(Environment.NewLine, ex.GetInnerExceptions().Select(e => e.Message))
    };
}
