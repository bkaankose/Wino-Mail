using Wino.Core.Domain;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Turns a <see cref="MapiProbeResult"/> into the text the account settings surfaces show, so the
/// "Test MAPI" result reads the same wherever it is offered.
/// </summary>
internal static class MapiProbeMessages
{
    public static string Success(MapiProbeResult result)
        => string.Format(Translator.SettingsEditAccountDetails_MapiProbe_Success,
            result.DisplayName, result.AuthMode, result.Endpoint, result.LegacyDn, result.FolderCount, result.ElapsedMs);

    public static string Failure(MapiProbeResult result)
    {
        var detail = string.IsNullOrEmpty(result.AuthMode)
            ? result.Detail
            : $"{result.Detail}\n\nAuth: {result.AuthMode}";

        return string.Format(Translator.SettingsEditAccountDetails_MapiProbe_Failure, result.Stage, detail);
    }
}
