using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Activation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Launch;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.Client.Shell;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Routes OS activations (mailto, wino://, webcal(s), opened .ics/.vcf/.eml files) the way the Windows
/// App activation paths do; an .eml opens read-only in its own reader window (<see cref="Views.Mail.EmlReaderWindow"/>). AppDelegate queues URLs until services are ready; handling is serialized.
/// </summary>
public sealed class MacActivationCoordinator(IServiceProvider services)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AppKitNavigationService Navigation => services.GetRequiredService<AppKitNavigationService>();
    private IMailDialogService Dialogs => services.GetRequiredService<IMailDialogService>();

    /// <summary>Handles URLs in the order the OS delivered them. Files of one kind are imported together.</summary>
    public async Task HandleAsync(IReadOnlyList<NSUrl> urls)
    {
        await _gate.WaitAsync();
        try
        {
            var calendarFiles = new List<NSUrl>();
            var contactFiles = new List<NSUrl>();
            var mailFiles = new List<NSUrl>();
            foreach (var url in urls)
            {
                var kind = Classify(url);
                switch (kind)
                {
                    case ActivationUriKind.CalendarFile: calendarFiles.Add(url); break;
                    case ActivationUriKind.ContactFile: contactFiles.Add(url); break;
                    case ActivationUriKind.MailFile: mailFiles.Add(url); break;
                    case ActivationUriKind.Unsupported:
                        Serilog.Log.Information("Ignoring unsupported activation {Scheme}.", url.Scheme);
                        break;
                    default:
                        await HandleUrlAsync(kind, url.AbsoluteString ?? string.Empty);
                        break;
                }
            }
            if (calendarFiles.Count > 0) await HandleFilesAsync(WinoApplicationMode.Calendar, calendarFiles);
            if (contactFiles.Count > 0) await HandleFilesAsync(WinoApplicationMode.Contacts, contactFiles);
            if (mailFiles.Count > 0) await HandleMailFilesAsync(mailFiles);
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "OS activation failed.");
        }
        finally { _gate.Release(); }
    }

    public static ActivationUriKind Classify(NSUrl url)
        => url.IsFileUrl ? ActivationUriClassifier.ClassifyFilePath(url.Path) : ActivationUriClassifier.Classify(url.AbsoluteString);

    private async Task HandleUrlAsync(ActivationUriKind kind, string value)
    {
        switch (kind)
        {
            case ActivationUriKind.MailTo:
                await HandleMailToAsync(value);
                break;
            case ActivationUriKind.BillingSuccess:
                // App.BillingActivation: Settings › Wino Account with the checkout reason (forces a profile refresh).
                await ShowSettingsAsync(WinoPage.WinoAccountManagementPage, WinoAccountManagementActivationReason.CheckoutCompleted);
                break;
            case ActivationUriKind.Webcal:
                // Windows opens the calendar for webcal links; subscribing is not implemented on either platform.
                if (!await HasAccountsAsync(Translator.FileActivation_NoWritableCalendarsMessage, Translator.FileActivation_ImportFailedTitle)) return;
                await Navigation.EnsureShellAsync(WinoApplicationMode.Calendar, new CalendarPageNavigationArgs { RequestDefaultNavigation = true });
                break;
        }
    }

    /// <summary>
    /// App.HandleMailToProtocolActivationAsync: the shell's mail launch path consumes
    /// <see cref="IActivationStateService.MailToUri"/> on cold start or mode switch; a running mail
    /// shell gets <see cref="MailtoProtocolMessageRequested"/>.
    /// </summary>
    private async Task HandleMailToAsync(string value)
    {
        MailToUri mailTo;
        try { mailTo = new MailToUri(value); }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Ignoring a malformed mailto link.");
            return;
        }
        if (!await HasAccountsAsync(Translator.DialogMessage_NoAccountsForCreateMailMessage, Translator.DialogMessage_NoAccountsForCreateMailTitle)) return;

        var navigation = Navigation;
        var mailReady = navigation.IsShellReady && navigation.Shell?.ActiveMode == WinoApplicationMode.Mail;
        services.GetRequiredService<IActivationStateService>().MailToUri = mailTo;
        if (!await navigation.EnsureShellAsync(WinoApplicationMode.Mail)) return;
        if (mailReady) WeakReferenceMessenger.Default.Send(new MailtoProtocolMessageRequested());
    }

    /// <summary>App.HandleFileActivationAsync. Finder-provided files are read inside their security scope.</summary>
    private async Task HandleFilesAsync(WinoApplicationMode mode, IReadOnlyList<NSUrl> files)
    {
        var noAccountsMessage = mode == WinoApplicationMode.Calendar
            ? Translator.FileActivation_NoWritableCalendarsMessage
            : Translator.FileActivation_NoContactDestinationMessage;
        if (!await HasAccountsAsync(noAccountsMessage, Translator.FileActivation_ImportFailedTitle)) return;

        var paths = files.Select(file => file.Path).Where(path => !string.IsNullOrEmpty(path)).Select(path => path!).ToList();
        var scoped = files.Where(file => file.StartAccessingSecurityScopedResource()).ToList();
        try
        {
            var importer = services.GetRequiredService<IActivationFileImportService>();
            if (mode == WinoApplicationMode.Calendar)
            {
                var composeArgs = await importer.ImportCalendarEventAsync(paths);
                object parameter = composeArgs is null ? new CalendarPageNavigationArgs { RequestDefaultNavigation = true } : composeArgs;
                if (!await Navigation.EnsureShellAsync(WinoApplicationMode.Calendar, parameter)) return;
                if (composeArgs is null)
                    Dialogs.InfoBarMessage(Translator.FileActivation_ImportFailedTitle, Translator.FileActivation_CalendarImportFailedMessage, InfoBarMessageType.Warning);
            }
            else
            {
                var draft = await importer.ImportContactAsync(paths);
                if (!await Navigation.EnsureShellAsync(WinoApplicationMode.Contacts, draft is null ? null : new ContactEditNavigationParameter(ImportDraft: draft))) return;
                if (draft is null)
                    Dialogs.InfoBarMessage(Translator.FileActivation_ImportFailedTitle, Translator.FileActivation_ContactImportFailedMessage, InfoBarMessageType.Warning);
            }
        }
        finally
        {
            foreach (var file in scoped) file.StopAccessingSecurityScopedResource();
        }
    }

    /// <summary>Most message windows one activation opens; the rest are logged and skipped.</summary>
    private const int MaxMailFileWindows = 10;

    /// <summary>
    /// Opened .eml files: each one in its own read-only reader window. No account is needed. The file is
    /// read inside its security scope, which ends before the reader renders from memory.
    /// </summary>
    private async Task HandleMailFilesAsync(IReadOnlyList<NSUrl> files)
    {
        var distinct = files.Where(file => !string.IsNullOrEmpty(file.Path))
            .DistinctBy(file => file.Path!, StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count > MaxMailFileWindows)
            Serilog.Log.Information("Opening the first {Count} of {Total} message files.", MaxMailFileWindows, distinct.Count);

        foreach (var file in distinct.Take(MaxMailFileWindows))
        {
            var path = file.Path!;
            byte[]? bytes = null;
            var scoped = file.StartAccessingSecurityScopedResource();
            try { bytes = await File.ReadAllBytesAsync(path); }
            catch (Exception error) { Serilog.Log.Warning(error, "Could not read an opened message file."); }
            finally { if (scoped) file.StopAccessingSecurityScopedResource(); }

            var opened = false;
            if (bytes is { Length: > 0 })
            {
                try { opened = await Views.Mail.EmlReaderWindow.OpenAsync(services, path, bytes); }
                catch (Exception error) { Serilog.Log.Error(error, "Could not open a message window."); }
            }

            if (!opened)
                await Dialogs.ShowMessageAsync(string.Format(Translator.MacPlatform_EmlOpenFailedMessage, Path.GetFileName(path)),
                    Translator.MacPlatform_EmlOpenFailedTitle, WinoCustomMessageDialogIcon.Warning);
        }
    }

    private async Task ShowSettingsAsync(WinoPage page, object? parameter)
    {
        var presenter = services.GetService<ISettingsWindowPresenter>();
        if (presenter is null)
        {
            Navigation.ChangeApplicationMode(WinoApplicationMode.Settings);
            return;
        }
        await presenter.ShowAsync(page, parameter);
    }

    /// <summary>Activations that need an account show a message and are dropped when none exists.</summary>
    private async Task<bool> HasAccountsAsync(string message, string title)
    {
        var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
        if (accounts.Count > 0) return true;
        await Dialogs.ShowMessageAsync(message, title, WinoCustomMessageDialogIcon.Warning);
        return false;
    }
}
