#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-signatures</c> (<c>as</c>): <c>['open', address]</c> opens the signature page of that account in Settings;
/// <c>['seed', address, name, htmlFile]</c> creates a signature; <c>['clear', address]</c> deletes the signatures whose
/// name starts with "Lab "; <c>['state', path]</c> writes every account's signature settings and signatures, read fresh
/// from the services.
/// </summary>
internal static class SignatureDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-signatures", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => Observe(RunAsync(services, args.Parameter?.GetStrv(out _) ?? []));
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var accounts = provider.GetRequiredService<IAccountService>();
        var signatures = provider.GetRequiredService<ISignatureService>();
        var op = parts.Length > 0 ? parts[0] : string.Empty;

        async Task<MailAccount> Account(string address)
            => (await accounts.GetAccountsAsync()).FirstOrDefault(a => string.Equals(a.Address, address, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"No account '{address}'.");

        switch (op)
        {
            case "open" when parts.Length >= 2:
                await provider.GetRequiredService<ISettingsWindowPresenter>().ShowAsync(WinoPage.SignatureManagementPage, (await Account(parts[1])).Id);
                break;
            case "seed" when parts.Length >= 4:
            {
                var account = await Account(parts[1]);
                var created = await signatures.CreateSignatureAsync(new AccountSignature
                {
                    Id = Guid.NewGuid(),
                    Name = parts[2],
                    HtmlBody = await File.ReadAllTextAsync(parts[3]),
                    MailAccountId = account.Id,
                });
                Serilog.Log.Information("Debug action: signature {Name} created ({Id}).", created.Name, created.Id);
                break;
            }
            case "clear" when parts.Length >= 2:
            {
                var account = await Account(parts[1]);
                var removed = 0;
                foreach (var signature in await signatures.GetSignaturesAsync(account.Id))
                {
                    if (signature.Name?.StartsWith("Lab ", StringComparison.Ordinal) != true) continue;
                    await signatures.DeleteSignatureAsync(signature);
                    removed++;
                }
                Serilog.Log.Information("Debug action: {Count} lab signatures deleted.", removed);
                break;
            }
            case "state" when parts.Length >= 2:
            {
                var text = new StringBuilder();
                foreach (var listed in await accounts.GetAccountsAsync())
                {
                    var account = await accounts.GetAccountAsync(listed.Id);
                    if (account is null) continue;
                    var preferences = account.Preferences;
                    text.AppendLine($"account={account.Address} enabled={preferences?.IsSignatureEnabled} new={preferences?.SignatureIdForNewMessages} following={preferences?.SignatureIdForFollowingMessages}");
                    foreach (var signature in await signatures.GetSignaturesAsync(account.Id))
                        text.AppendLine($"signature={signature.Id} | {signature.Name} | {signature.HtmlBody?.Length ?? 0}");
                }
                var path = parts[1];
                await File.WriteAllTextAsync(path + ".partial", text.ToString());
                File.Move(path + ".partial", path, true);
                Serilog.Log.Information("Debug action: signature state written to {Path}.", path);
                break;
            }
            default:
                throw new ArgumentException($"Unknown debug-signatures operation '{string.Join(' ', parts)}'.");
        }
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-signatures failed."); }
    }
}
#endif
