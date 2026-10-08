#if DEBUG
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Debug bridge commands for the Wino Account sheets and page. The sheets open without waiting, so
/// the bridge stays free for <c>snap</c>; <c>dlg-result</c> reads how the last one ended.
/// <list type="bullet">
/// <item><c>dlg-purchase</c>: the purchase channel sheet.</item>
/// <item><c>dlg-secret new|existing|legacy|rejected</c>: the sync secret sheet (the entered value is never reported).</item>
/// <item><c>dlg-cert &lt;pem-or-der-path&gt;</c>: the certificate trust sheet with a sample summary.</item>
/// <item><c>winoacct-state</c>: the open Wino Account page's ViewModel flags.</item>
/// <item><c>winoacct-checkout</c>: opens the page as the billing return does (CheckoutCompleted).</item>
/// <item><c>winoacct-benefit &lt;index&gt;</c>: selects a signed-out offer.</item>
/// </list>
/// </summary>
internal static class WinoAccountDebug
{
    private static string _lastResult = "none";

    public static WeakReference<WinoAccountManagementPageViewModel>? Current { get; set; }

    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    private static IMailDialogService Dialogs => Services.GetRequiredService<IMailDialogService>();

    public static void Register()
    {
        MacDebugBridge.Register("dlg-result", _ => Task.FromResult(_lastResult));
        MacDebugBridge.Register("dlg-purchase", _ => Run("purchase", async () => (await Dialogs.ShowUnlimitedAccountsPurchaseChannelDialogAsync())?.ToString() ?? "cancelled"));
        MacDebugBridge.Register("dlg-secret", args =>
        {
            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "existing";
            var request = mode switch
            {
                "new" => new SyncSnapshotSecretRequest(IsPassphrase: true, WasRejected: false, IsNewBackup: true),
                "legacy" => new SyncSnapshotSecretRequest(IsPassphrase: false, WasRejected: false),
                "rejected" => new SyncSnapshotSecretRequest(IsPassphrase: true, WasRejected: true),
                _ => new SyncSnapshotSecretRequest(IsPassphrase: true, WasRejected: false)
            };
            // Only whether a value came back is reported; the secret itself is dropped here.
            return Run("secret " + mode, async () => await Dialogs.ShowWinoAccountSyncSecretDialogAsync(request) is null ? "cancelled" : "entered");
        });
        MacDebugBridge.Register("dlg-cert", args =>
        {
            if (args.Length == 0) return Task.FromResult("usage: dlg-cert <pem-or-der-path>");
            var path = string.Join(' ', args);
            if (!File.Exists(path)) return Task.FromResult("no file: " + path);
            var raw = LoadCertificateBytes(path);
            using var certificate = X509CertificateLoader.LoadCertificate(raw);
            var summary = SampleSummary(certificate);
            return Run("cert", async () => await Dialogs.ShowServerCertificateTrustDialogAsync(summary, raw) ? "trusted" : "cancelled");
        });
        MacDebugBridge.Register("winoacct-state", _ => Task.FromResult(State()));
        MacDebugBridge.Register("winoacct-checkout", _ =>
            Task.FromResult(Services.GetRequiredService<AppKitNavigationService>().Navigate(WinoPage.WinoAccountManagementPage, WinoAccountManagementActivationReason.CheckoutCompleted) ? "ok" : "refused"));
        MacDebugBridge.Register("winoacct-benefit", args =>
        {
            if (Current?.TryGetTarget(out var vm) != true || vm is null) return Task.FromResult("no page");
            var index = args.Length > 0 && int.TryParse(args[0], out var value) ? value : 0;
            if (index < 0 || index >= vm.Benefits.Count) return Task.FromResult("out of range");
            vm.SelectedBenefit = vm.Benefits[index];
            return Task.FromResult("ok");
        });
    }

    private static Task<string> Run(string name, Func<Task<string>> show)
    {
        _lastResult = name + ": open";
        _ = Task.Run(async () =>
        {
            try { _lastResult = name + ": " + await show(); }
            catch (Exception exception) { _lastResult = name + ": error " + exception.Message; }
        });
        return Task.FromResult("ok");
    }

    private static byte[] LoadCertificateBytes(string path)
    {
        var text = File.ReadAllText(path);
        return text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(text).RawData
            : File.ReadAllBytes(path);
    }

    /// <summary>The same shape the account ViewModels send, so the sheet lays out real rows.</summary>
    private static string SampleSummary(X509Certificate2 certificate)
    {
        return $"{Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row0}\n\n" +
               $"{Translator.IMAPSetupDialog_CertificateProtocol}: IMAP\n" +
               $"{Translator.IMAPSetupDialog_CertificateEndpoint}: mail.example.test:993\n" +
               $"{Translator.IMAPSetupDialog_CertificateSubject}: {certificate.Subject}\n" +
               $"{Translator.IMAPSetupDialog_CertificateSans}: mail.example.test\n" +
               $"{Translator.IMAPSetupDialog_CertificateIssuer}: {certificate.Issuer}\n" +
               $"{Translator.IMAPSetupDialog_CertificateValidFrom}: {certificate.NotBefore.ToUniversalTime():u}\n" +
               $"{Translator.IMAPSetupDialog_CertificateValidTo}: {certificate.NotAfter.ToUniversalTime():u}\n" +
               $"{Translator.IMAPSetupDialog_CertificateFingerprint}: {certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)}\n" +
               $"{Translator.IMAPSetupDialog_CertificateFailureReason}: UntrustedRoot\n\n" +
               Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row1;
    }

    private static string State()
    {
        if (Current?.TryGetTarget(out var vm) != true || vm is null) return "no page";
        return string.Join(" ",
            $"signedIn={vm.IsSignedIn}",
            $"busy={vm.IsBusy}",
            $"profileBusy={vm.IsProfileBusy}",
            $"checkout={vm.IsCheckoutInProgress}",
            $"refreshing={vm.IsIntelligenceRefreshing}",
            $"intelligence={vm.HasIntelligenceAccess}",
            $"aiPack={(vm.AiPackAddOn.IsPurchased ? "purchased" : vm.AiPackAddOn.IsLoading ? "loading" : "not-purchased")}",
            $"unlimited={(vm.UnlimitedAccountsAddOn.IsPurchased ? "purchased" : vm.UnlimitedAccountsAddOn.IsLoading ? "loading" : "not-purchased")}",
            $"usage={vm.IntelligenceUsagePercentage:0}%",
            $"accounts=\"{vm.AccountUsageText}\"",
            $"storeRedeem={vm.ShowStoreRedeemCard}",
            $"benefit={vm.SelectedBenefit?.Type}",
            $"purchaseStatus=\"{vm.PurchaseStatusMessage}\"",
            $"refreshError=\"{vm.IntelligenceRefreshError}\"");
    }
}
#endif
