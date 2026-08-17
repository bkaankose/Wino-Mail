namespace Wino.Intelligence.ConsoleApp;

internal static class StressCommandLine
{
    public static bool TryParse(string[] args, out StressOptions? options, out string? error)
    {
        options = null;
        error = null;
        if (!args.Contains("--stress", StringComparer.OrdinalIgnoreCase)) return true;

        var environment = ApiEnvironment.Local;
        var profile = StressProfile.Realistic;
        string? account = null;
        string? output = null;
        double startRps = 1;
        double maxRps = 256;
        var maxConcurrency = 512;
        var stageDuration = TimeSpan.FromMinutes(5);
        var sustainDuration = TimeSpan.FromMinutes(60);
        int? aiRequestLimit = null;
        var confirmed = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--stress") continue;
            if (argument == "--confirm-production-stress") { confirmed = true; continue; }
            if (!TryReadValue(args, ref index, out var value))
                return Fail($"Unknown or incomplete stress argument: {argument}", out options, out error);

            switch (argument)
            {
                case "--environment" when Enum.TryParse<ApiEnvironment>(value, true, out var parsedEnvironment): environment = parsedEnvironment; break;
                case "--account": account = value; break;
                case "--profile" when Enum.TryParse<StressProfile>(value, true, out var parsedProfile): profile = parsedProfile; break;
                case "--start-rps" when double.TryParse(value, out startRps): break;
                case "--max-rps" when double.TryParse(value, out maxRps): break;
                case "--max-concurrency" when int.TryParse(value, out maxConcurrency): break;
                case "--stage-duration" when double.TryParse(value, out var stageMinutes): stageDuration = TimeSpan.FromMinutes(stageMinutes); break;
                case "--sustain-duration" when double.TryParse(value, out var sustainMinutes): sustainDuration = TimeSpan.FromMinutes(sustainMinutes); break;
                case "--ai-request-limit" when int.TryParse(value, out var limit): aiRequestLimit = limit; break;
                case "--output": output = value; break;
                default: return Fail($"Invalid stress argument or value: {argument} {value}", out options, out error);
            }
        }

        if (string.IsNullOrWhiteSpace(account)) return Fail("--account is required for stress mode.", out options, out error);
        if (string.IsNullOrWhiteSpace(output)) return Fail("--output is required for stress mode.", out options, out error);
        if (startRps <= 0 || maxRps < startRps) return Fail("RPS values must be positive and max RPS must be at least start RPS.", out options, out error);
        if (maxConcurrency <= 0 || stageDuration <= TimeSpan.Zero || sustainDuration < TimeSpan.Zero)
            return Fail("Concurrency and durations must be positive.", out options, out error);
        if (profile == StressProfile.Ai && aiRequestLimit is not > 0)
            return Fail("--ai-request-limit is required and must be positive for the AI profile.", out options, out error);
        if (environment == ApiEnvironment.Production && !confirmed)
            return Fail("Production stress mode requires --confirm-production-stress.", out options, out error);

        options = new StressOptions(environment, account.Trim(), profile, startRps, maxRps, maxConcurrency,
            stageDuration, sustainDuration, aiRequestLimit, Path.GetFullPath(output), confirmed);
        return true;
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) return false;
        value = args[++index];
        return true;
    }

    private static bool Fail(string message, out StressOptions? options, out string? error)
    {
        options = null;
        error = message;
        return false;
    }
}
