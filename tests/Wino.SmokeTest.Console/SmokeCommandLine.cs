namespace Wino.SmokeTest.ConsoleApp;

internal static class SmokeCommandLine
{
    public static bool TryParse(string[] args, out SmokeCommandLineOptions options, out string? error)
    {
        string? account = null;
        string? reportTo = null;
        string? attachmentsFolder = null;
        string? publisherFolder = null;
        string? applicationDataFolder = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--smoke":
                    break;
                case "--account" when index + 1 < args.Length:
                    account = args[++index];
                    break;
                case "--report-to" when index + 1 < args.Length:
                    reportTo = args[++index];
                    break;
                case "--attachments-folder" when index + 1 < args.Length:
                    attachmentsFolder = args[++index];
                    break;
                case "--publisher-folder" when index + 1 < args.Length:
                    publisherFolder = args[++index];
                    break;
                case "--app-data-folder" when index + 1 < args.Length:
                    applicationDataFolder = args[++index];
                    break;
                default:
                    options = default!;
                    error = $"Unknown or incomplete smoke argument: {args[index]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(account))
        {
            options = default!;
            error = "--smoke requires --account <email>.";
            return false;
        }

        options = new SmokeCommandLineOptions(
            account.Trim(),
            string.IsNullOrWhiteSpace(reportTo) ? null : reportTo.Trim(),
            Path.GetFullPath(attachmentsFolder ?? Path.Combine(Environment.CurrentDirectory, "Attachments")),
            publisherFolder,
            applicationDataFolder);
        error = null;
        return true;
    }
}

internal sealed record SmokeCommandLineOptions(
    string AccountAddress,
    string? ReportRecipient,
    string AttachmentsFolder,
    string? PublisherFolder,
    string? ApplicationDataFolder);
