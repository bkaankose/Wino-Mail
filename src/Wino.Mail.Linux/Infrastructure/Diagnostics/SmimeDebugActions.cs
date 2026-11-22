#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MimeKit.Cryptography;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;
using Wino.Services.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-smime</c> <c>as</c> (PL-05a): import personal|recipient path [password] (the service directly), list,
/// remove personal|recipient thumbprint, context-test address (sign + verify and encrypt + decrypt a small part),
/// page-state, page-select personal|recipient i,j, page-import personal|recipient (the page's flow: answer the picker
/// with <c>debug-accounts ['pick', path]</c> first), page-export personal|recipient, password text|cancel (answers the
/// open password dialog); PL-05b: compose-state, compose-sign|compose-encrypt on|off (the toolbar toggles), compose-cert i
/// (the certificate drop-down), alias-state, alias-encrypt address on|off, alias-cert address i (0 = None; the open
/// alias page's ViewModel writes). One log line <c>Debug action debug-smime &lt;verb&gt;: &lt;result&gt;</c> (never the password);
/// a last argument ending in .txt also receives it.
/// </summary>
internal static class SmimeDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-smime", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(services, args.Parameter?.GetStrv(out _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] arguments)
    {
        var verb = arguments.Length > 0 ? arguments[0] : string.Empty;
        string? path = arguments.Length > 1 && arguments[^1].EndsWith(".txt", StringComparison.Ordinal) ? arguments[^1] : null;
        var rest = arguments.Length > 1 ? arguments[1..(path is null ? arguments.Length : arguments.Length - 1)] : [];
        string result;
        try
        {
            var provider = services() ?? throw new InvalidOperationException("no services");
            var smime = provider.GetRequiredService<ISmimeCertificateService>();
            result = verb switch
            {
                "import" => Import(smime, rest),
                "list" => List(smime),
                "remove" => Remove(smime, rest),
                "context-test" => await Task.Run(() => ContextTest(smime, rest.Length > 0 ? rest[0] : string.Empty)),
                "page-state" => Page().DebugSection(SmimeCertificatePurpose.Personal).Describe() + " || " + Page().DebugSection(SmimeCertificatePurpose.Recipient).Describe(),
                "page-select" => PageSelect(rest),
                "page-import" => Started(Page().DebugImportAsync(Purpose(rest))),
                "page-export" => Started(Page().DebugExportAsync(Purpose(rest))),
                "password" => Password(rest),
                "compose-state" => Composer().DebugSmime(),
                "compose-sign" or "compose-encrypt" => ComposeToggle(verb == "compose-encrypt", rest),
                "compose-cert" => ComposeCertificate(rest),
                "alias-state" => Aliases().DebugSmime(),
                "alias-encrypt" => await AliasWrite(Aliases().DebugSetEncryptionAsync(Arg(rest, 0), Arg(rest, 1) == "on")),
                "alias-cert" => await AliasWrite(Aliases().DebugSetCertificateAsync(Arg(rest, 0), int.Parse(Arg(rest, 1), System.Globalization.CultureInfo.InvariantCulture))),
                _ => "verbs: import, list, remove, context-test, page-state, page-select, page-import, page-export, password, compose-state, compose-sign, compose-encrypt, compose-cert, alias-state, alias-encrypt, alias-cert",
            };
        }
        catch (Exception exception)
        {
            result = "error: " + exception.Message;
            Serilog.Log.Error(exception, "Debug action debug-smime {Verb} failed.", verb);
        }
        Serilog.Log.Information("Debug action debug-smime {Verb}: {Result}", verb, result);
        if (path is not null) DebugActions.WriteText(path, result);
    }

    private static SignatureAndEncryptionPage Page() => SignatureAndEncryptionPage.DebugCurrent ?? throw new InvalidOperationException("the page is not open");

    private static Views.Mail.Compose.ComposeView Composer() => ComposeDebugActions.Current ?? throw new InvalidOperationException("no composer is shown");

    private static AliasManagementPage Aliases() => AliasManagementPage.DebugCurrent ?? throw new InvalidOperationException("the alias page is not open");

    private static string Arg(string[] rest, int index) => index < rest.Length ? rest[index] : string.Empty;

    private static string ComposeToggle(bool encrypt, string[] rest)
    {
        var view = Composer();
        view.DebugToggle(encrypt, Arg(rest, 0) == "on");
        return view.DebugSmime();
    }

    private static string ComposeCertificate(string[] rest)
    {
        var view = Composer();
        view.DebugSelectCertificate(int.Parse(Arg(rest, 0), System.Globalization.CultureInfo.InvariantCulture));
        return view.DebugSmime();
    }

    private static async Task<string> AliasWrite(Task write)
    {
        await write;
        return "written";
    }

    private static SmimeCertificatePurpose Purpose(string[] rest)
        => rest.Length > 0 && rest[0] == "recipient" ? SmimeCertificatePurpose.Recipient : SmimeCertificatePurpose.Personal;

    private static string Started(Task task)
    {
        _ = task.ContinueWith(static t => Serilog.Log.Error(t.Exception, "A debug S/MIME page flow failed."), TaskContinuationOptions.OnlyOnFaulted);
        return "started";
    }

    private static string Import(ISmimeCertificateService smime, string[] rest)
    {
        if (rest.Length < 2) return "import personal|recipient path [password]";
        smime.ImportCertificate(Path.GetExtension(rest[1]), File.ReadAllBytes(rest[1]), rest.Length > 2 ? rest[2] : null, Purpose(rest));
        return "imported " + Path.GetFileName(rest[1]);
    }

    private static string List(ISmimeCertificateService smime)
    {
        var lines = new List<string>();
        foreach (var purpose in (SmimeCertificatePurpose[])[SmimeCertificatePurpose.Personal, SmimeCertificatePurpose.Recipient])
        {
            foreach (var certificate in smime.GetCertificates(purpose))
            {
                using (certificate) lines.Add($"{purpose} {certificate.Subject} key={certificate.HasPrivateKey} {certificate.Thumbprint}");
            }
        }
        return lines.Count == 0 ? "empty" : string.Join(" | ", lines);
    }

    private static string Remove(ISmimeCertificateService smime, string[] rest)
    {
        if (rest.Length < 2) return "remove personal|recipient thumbprint";
        smime.RemoveCertificate(rest[1], Purpose(rest));
        return "removed";
    }

    private static string ContextTest(ISmimeCertificateService smime, string address)
    {
        var mailbox = new MailboxAddress(address, address);
        var body = new TextPart("plain") { Text = "Wino S/MIME check" };
        using var context = smime.CreateContext();
        var signed = MultipartSigned.Create(context, mailbox, DigestAlgorithm.Sha256, body);
        var valid = signed.Verify(context).All(static signature => signature.Verify());
        var encrypted = ApplicationPkcs7Mime.Encrypt(context, [mailbox], body);
        var decrypted = (encrypted.Decrypt(context) as TextPart)?.Text;
        return $"signed valid={valid} decrypted={decrypted == body.Text}";
    }

    private static string PageSelect(string[] rest)
    {
        var positions = rest.Length > 1 ? rest[1].Split(',').Select(int.Parse).ToArray() : [];
        var section = Page().DebugSection(Purpose(rest));
        section.Select(positions);
        return section.Describe();
    }

    private static string Password(string[] rest)
    {
        if (Infrastructure.GtkDialogService.DebugSmimePassword is not { } form) return "no password dialog";
        if (rest.Length > 0 && rest[0] == "cancel")
        {
            form.Close(false);
            return "cancelled";
        }
        if (FindPassword(form.Content) is { } row) row.SetText(rest.Length > 0 ? rest[0] : string.Empty);
        form.DebugConfirm();
        return "confirmed";
    }

    private static Adw.PasswordEntryRow? FindPassword(Gtk.Widget widget)
    {
        if (widget is Adw.PasswordEntryRow row) return row;
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            if (FindPassword(child) is { } found) return found;
        }
        return null;
    }
}
#endif
