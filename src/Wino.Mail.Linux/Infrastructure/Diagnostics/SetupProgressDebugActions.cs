#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Onboarding;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-validation-dialog ''</c> shows the validation-failed dialog with a sample error and a 200-line sample log;
/// <c>debug-creation-dialog 'cancel'|'complete'|''</c> shows the account creation dialog, or ends it as Cancel or as the
/// caller would; <c>debug-progress-state 'path'</c> writes the progress page; <c>debug-exchange-state 'path'</c>,
/// <c>debug-exchange-type "[field, text]"</c> and <c>debug-exchange-primary ''</c> drive the Exchange page;
/// <c>debug-cert-dialog 'path'</c> writes the open certificate question, <c>debug-cert-ack 'on'|'off'</c> ticks its
/// acknowledgement (AT-SPI cannot toggle a check box).
/// </summary>
internal static class SetupProgressDebugActions
{
    private static IAccountCreationDialog? _creation;
    private static CancellationTokenSource? _creationCancellation;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-validation-dialog", _ =>
        {
            var log = string.Join('\n', Enumerable.Range(1, 200).Select(line => $"S: * {line} EXISTS (sample protocol line {line} for the scroll check)"));
            Observe(Provider(services).GetRequiredService<IMailDialogService>().ShowImapValidationFailedDialogAsync("IMAP server validation failed: Authentication failed (sample)", log));
        });
        Add(application, "debug-creation-dialog", value =>
        {
            switch (value)
            {
                case "cancel": _creation?.Complete(true); return;
                case "complete": _creation?.Complete(false); return;
            }
            _creationCancellation = new CancellationTokenSource();
            _creationCancellation.Token.Register(() => Serilog.Log.Information("Debug action: account creation was cancelled."));
            _creation = Provider(services).GetRequiredService<IMailDialogService>().GetAccountCreationDialog(new Wino.Core.Domain.Models.Accounts.AccountCreationDialogResult(MailProviderType.IMAP4, "Debug", null, "#000000", InitialSynchronizationRange.SixMonths, true, false));
            _creation.State = AccountCreationDialogState.TestingConnection;
            Observe(_creation.ShowDialogAsync(_creationCancellation));
        });
        Add(application, "debug-progress-state", value => DebugActions.WriteText(value,
            AccountSetupProgressView.DebugCurrent?.DebugDescribe() ?? "not shown\n"));
        Add(application, "debug-exchange-state", value => DebugActions.WriteText(value,
            ExchangeSettingsView.DebugCurrent?.DebugDescribe() ?? "not shown\n"));
        var type = Gio.SimpleAction.New("debug-exchange-type", GLib.VariantType.New("as"));
        type.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            if (parts.Length < 2 || ExchangeSettingsView.DebugCurrent is not { } view) return;
            Serilog.Log.Information("Debug action: Exchange field {Field} typed={Ok}.", parts[0], view.DebugType(parts[0], AccountSetupDebugActions.LabValue(parts[1])));
        };
        application.AddAction(type);
        Add(application, "debug-cert-dialog", value => DebugActions.WriteText(value,
            Wino.Mail.Linux.Views.Dialogs.ServerCertificateTrustDialog.DebugCurrent?.DebugDescribe() ?? "not shown\n"));
        Add(application, "debug-cert-ack", value => Wino.Mail.Linux.Views.Dialogs.ServerCertificateTrustDialog.DebugCurrent?.DebugAcknowledge(value == "on"));
        Add(application, "debug-exchange-primary", _ =>
        {
            if (ExchangeSettingsView.DebugCurrent?.DebugViewModel.PrimaryCommand is { } command && command.CanExecute(null)) Observe(command.ExecuteAsync(null));
        });
    }

    private static IServiceProvider Provider(Func<IServiceProvider?> services) => services() ?? throw new InvalidOperationException("The app has not started.");

    private static void Add(Adw.Application application, string name, Action<string> run)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New("s"));
        action.OnActivate += (sender, args) =>
        {
            try { run(args.Parameter?.GetString(out _) ?? string.Empty); }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Action} failed.", name); }
        };
        application.AddAction(action);
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Setup progress debug action failed."); }
    }
}
#endif
