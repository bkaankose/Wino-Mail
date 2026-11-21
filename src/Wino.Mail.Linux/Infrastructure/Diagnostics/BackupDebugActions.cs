#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Settings;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-backup</c> (<c>as</c>), the file picker bypass for Backup and restore with the calls the page's ViewModel makes:
/// <c>['export', path, passwordFile]</c>, <c>['import', path, passwordFile]</c> (a rejected password is answered once
/// more with the same value, then cancelled), <c>['secret', 'new'|'unlock'|'rejected']</c> opens the real prompt and logs
/// only whether it was answered and the answer's length. Passwords come from files and are never logged.
/// </summary>
internal static class BackupDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-backup", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            Observe(RunAsync(services, parts));
        };
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var op = parts.Length > 0 ? parts[0] : string.Empty;
        switch (op)
        {
            case "export" when parts.Length >= 3:
            {
                var password = (await File.ReadAllTextAsync(parts[2])).TrimEnd('\n', '\r');
                var appearance = provider.GetRequiredService<AppearanceSnapshotService>();
                var result = await provider.GetRequiredService<IWinoAccountDataSyncService>().ExportToFileAsync(
                    new() { Appearance = appearance.Capture() }, _ => Task.FromResult<string?>(password));
                await File.WriteAllBytesAsync(parts[1], result.Content);
                var export = result.ExportResult;
                Serilog.Log.Information("Debug action: backup exported {Bytes} bytes, preferences {Preferences}, accounts {Accounts}, mailboxes {Mailboxes}, account data {AccountData}, app data {AppData}.",
                    result.Content.Length, export.IncludedPreferences, export.IncludedAccounts, export.ExportedMailboxCount, export.ExportedAccountDataCount, export.ExportedAppDataCount);
                break;
            }
            case "import" when parts.Length >= 3:
            {
                var password = (await File.ReadAllTextAsync(parts[2])).TrimEnd('\n', '\r');
                var bytes = await File.ReadAllBytesAsync(parts[1]);
                var asked = 0;
                Task<string?> Prompt(SyncSnapshotSecretRequest request)
                {
                    asked++;
                    Serilog.Log.Information("Debug action: backup password asked (rejected {Rejected}).", request.WasRejected);
                    return Task.FromResult<string?>(request.WasRejected && asked > 2 ? null : password);
                }
                var result = await provider.GetRequiredService<IWinoAccountDataSyncService>().ImportFromFileAsync(bytes, Prompt);
                if (result.Appearance is { } snapshot)
                {
                    var appearance = provider.GetRequiredService<AppearanceSnapshotService>();
                    await provider.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => appearance.Apply(snapshot));
                }
                Serilog.Log.Information("Debug action: backup imported, preferences applied {Applied}, failed {Failed}, mailboxes {Mailboxes} (skipped {Skipped}), account data {AccountData}.",
                    result.AppliedPreferenceCount, result.FailedPreferenceCount, result.ImportedMailboxCount, result.SkippedDuplicateMailboxCount, result.AppliedAccountDataCount);
                break;
            }
            case "secret" when parts.Length >= 2:
            {
                var request = parts[1] switch
                {
                    "new" => new SyncSnapshotSecretRequest(IsPassphrase: true, WasRejected: false, IsNewBackup: true),
                    "rejected" => new SyncSnapshotSecretRequest(IsPassphrase: true, WasRejected: true),
                    _ => new SyncSnapshotSecretRequest(IsPassphrase: true, WasRejected: false),
                };
                var answer = await provider.GetRequiredService<IMailDialogService>().ShowWinoAccountSyncSecretDialogAsync(request);
                Serilog.Log.Information("Debug action: backup secret {State}, length {Length}.", answer is null ? "cancelled" : "answered", answer?.Length ?? 0);
                break;
            }
            default:
                throw new ArgumentException($"Unknown debug-backup operation '{string.Join(' ', parts)}'.");
        }
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-backup failed."); }
    }
}
#endif
