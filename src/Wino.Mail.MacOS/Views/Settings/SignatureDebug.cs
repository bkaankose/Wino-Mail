#if DEBUG
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Debug bridge command <c>dlg-signature [new|edit]</c>: opens the signature editor sheet. "edit" opens the
/// first stored signature of the first account, or a sample one when there is none. The outcome is logged
/// to the console only; nothing is saved.
/// </summary>
internal static class SignatureDebug
{
    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    public static void Register()
    {
        MacDebugBridge.Register("dlg-signature", async args =>
        {
            AccountSignature? existing = null;
            if (args.Length > 0 && args[0].Equals("edit", StringComparison.OrdinalIgnoreCase))
            {
                var account = (await Services.GetRequiredService<IAccountService>().GetAccountsAsync())?.FirstOrDefault();
                if (account is not null)
                    existing = (await Services.GetRequiredService<ISignatureService>().GetSignaturesAsync(account.Id)).FirstOrDefault();
                existing ??= new AccountSignature
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = account?.Id ?? Guid.Empty,
                    Name = "Personal",
                    HtmlBody = "<p><b>Burak Kaan Köse</b></p><p>Developer · Wino Mail</p><p><a href=\"https://winomail.app\">winomail.app</a></p><p><br></p><p><span style=\"font-size:12px;color:#666666\">Sent from Wino Mail for Mac</span></p>"
                };
            }
            Show(existing);
            return "ok";
        });
    }

    /// <summary>The sheet stays open while the bridge returns, so its outcome goes to the console only.</summary>
    private static async void Show(AccountSignature? existing)
    {
        try
        {
            var result = await Services.GetRequiredService<IMailDialogService>().ShowSignatureEditorDialog(existing);
            Console.WriteLine(result is null
                ? "[signature-debug] cancelled"
                : $"[signature-debug] id={result.Id} account={result.MailAccountId} name={result.Name} html={result.HtmlBody?.Length ?? 0} chars");
        }
        catch (Exception exception) { Console.WriteLine($"[signature-debug] failed: {exception.Message}"); }
    }
}
#endif
