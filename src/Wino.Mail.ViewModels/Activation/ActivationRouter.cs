#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Wino.Core.Activation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Launch;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.Client.Shell;

namespace Wino.Mail.ViewModels.Activation;

/// <summary>
/// A file the OS handed to the app. Sandboxed hosts (security-scoped URLs on macOS and iOS) open
/// access around every read; elsewhere <see cref="BeginAccess"/> returns a no-op scope.
/// </summary>
public interface IActivationFile
{
    /// <summary>The local file system path, or null when the file has none.</summary>
    string? Path { get; }

    /// <summary>Starts reading access; disposing the result ends it.</summary>
    IDisposable BeginAccess();
}

/// <summary>One OS activation: a classified URL (mailto, wino://, webcal) or an opened file.</summary>
/// <param name="Kind">The classification from <see cref="ActivationUriClassifier"/>.</param>
/// <param name="Value">The absolute URL string, used for URL activations.</param>
/// <param name="File">The opened file, for file activations; null for URL schemes.</param>
public sealed record ActivationItem(ActivationUriKind Kind, string Value, IActivationFile? File = null);

/// <summary>
/// Routes OS activations (mailto, wino://, webcal(s), opened .ics/.vcf/.eml files, shared files) the way
/// the Windows App activation paths do. Files of one kind are handled together; activations that need an
/// account show a message and stop when none exists. Handling is serialized. The head classifies native
/// URLs into <see cref="ActivationItem"/> and presents message files through <see cref="IApplicationShellHost"/>.
/// </summary>
public sealed class ActivationRouter(
    IServiceProvider services,
    IApplicationShellHost host,
    IAccountService accountService,
    IMailDialogService dialogs,
    IActivationStateService activationState,
    IActivationFileImportService importer,
    IDispatcher dispatcher)
{
    /// <summary>Most message windows one activation opens; the rest are logged and skipped.</summary>
    public const int MaxMailFileWindows = 10;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<ActivationRouter>();

    /// <summary>The activations grouped the way <see cref="HandleAsync"/> routes them.</summary>
    public sealed record ActivationGroups(
        IReadOnlyList<ActivationItem> Urls,
        IReadOnlyList<IActivationFile> CalendarFiles,
        IReadOnlyList<IActivationFile> ContactFiles,
        IReadOnlyList<IActivationFile> MailFiles,
        IReadOnlyList<IActivationFile> AttachmentFiles,
        IReadOnlyList<ActivationItem> Ignored);

    /// <summary>
    /// Splits activations into URL routes (kept in delivery order) and file groups. Unsupported files become
    /// attachments of a new mail, like the Windows share target; unsupported URLs are ignored.
    /// </summary>
    public static ActivationGroups Group(IEnumerable<ActivationItem> items)
    {
        var urls = new List<ActivationItem>();
        var calendarFiles = new List<IActivationFile>();
        var contactFiles = new List<IActivationFile>();
        var mailFiles = new List<IActivationFile>();
        var attachmentFiles = new List<IActivationFile>();
        var ignored = new List<ActivationItem>();

        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case ActivationUriKind.CalendarFile when item.File is not null: calendarFiles.Add(item.File); break;
                case ActivationUriKind.ContactFile when item.File is not null: contactFiles.Add(item.File); break;
                case ActivationUriKind.MailFile when item.File is not null: mailFiles.Add(item.File); break;
                case ActivationUriKind.Unsupported when item.File is not null: attachmentFiles.Add(item.File); break;
                case ActivationUriKind.MailTo or ActivationUriKind.BillingSuccess or ActivationUriKind.Webcal: urls.Add(item); break;
                default: ignored.Add(item); break;
            }
        }

        return new ActivationGroups(urls, calendarFiles, contactFiles, mailFiles, attachmentFiles, ignored);
    }

    /// <summary>Handles activations in the order the OS delivered them. Files of one kind are imported together.</summary>
    public async Task HandleAsync(IReadOnlyList<ActivationItem> items)
    {
        await _gate.WaitAsync();
        try
        {
            var groups = Group(items);

            foreach (var item in groups.Ignored)
                _logger.Information("Ignoring unsupported activation {Kind}.", item.Kind);

            foreach (var url in groups.Urls)
                await HandleUrlAsync(url.Kind, url.Value);

            if (groups.CalendarFiles.Count > 0) await HandleFilesAsync(WinoApplicationMode.Calendar, groups.CalendarFiles);
            if (groups.ContactFiles.Count > 0) await HandleFilesAsync(WinoApplicationMode.Contacts, groups.ContactFiles);
            if (groups.MailFiles.Count > 0) await HandleMailFilesAsync(groups.MailFiles);
            if (groups.AttachmentFiles.Count > 0) await HandleAttachmentFilesAsync(groups.AttachmentFiles);
        }
        catch (Exception error)
        {
            _logger.Error(error, "OS activation failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Files shared to Wino (Services menu, share sheet): every file is an attachment, whatever its type.</summary>
    public async Task AttachFilesAsync(IReadOnlyList<IActivationFile> files)
    {
        await _gate.WaitAsync();
        try
        {
            await HandleAttachmentFilesAsync(files);
        }
        catch (Exception error)
        {
            _logger.Error(error, "Attaching shared files failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HandleUrlAsync(ActivationUriKind kind, string value)
    {
        switch (kind)
        {
            case ActivationUriKind.MailTo:
                await HandleMailToAsync(value);
                break;
            case ActivationUriKind.BillingSuccess:
                // App.BillingActivation: Settings › Wino Account with the checkout reason (forces a profile refresh).
                await host.ShowSettingsAsync(WinoPage.WinoAccountManagementPage, WinoAccountManagementActivationReason.CheckoutCompleted);
                break;
            case ActivationUriKind.Webcal:
                // Windows opens the calendar for webcal links; subscribing is not implemented on any platform.
                if (!await HasAccountsAsync(Translator.FileActivation_NoWritableCalendarsMessage, Translator.FileActivation_ImportFailedTitle)) return;
                await host.EnsureShellAsync(WinoApplicationMode.Calendar, new CalendarPageNavigationArgs { RequestDefaultNavigation = true });
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
        try
        {
            mailTo = new MailToUri(value);
        }
        catch (Exception error)
        {
            _logger.Warning(error, "Ignoring a malformed mailto link.");
            return;
        }

        if (!await HasAccountsAsync(Translator.DialogMessage_NoAccountsForCreateMailMessage, Translator.DialogMessage_NoAccountsForCreateMailTitle)) return;

        var mailReady = host.IsShellActive(WinoApplicationMode.Mail);
        activationState.MailToUri = mailTo;

        if (!await host.EnsureShellAsync(WinoApplicationMode.Mail)) return;
        if (mailReady) WeakReferenceMessenger.Default.Send(new MailtoProtocolMessageRequested());
    }

    /// <summary>App.HandleFileActivationAsync. Files are read inside their access scope.</summary>
    private async Task HandleFilesAsync(WinoApplicationMode mode, IReadOnlyList<IActivationFile> files)
    {
        var noAccountsMessage = mode == WinoApplicationMode.Calendar
            ? Translator.FileActivation_NoWritableCalendarsMessage
            : Translator.FileActivation_NoContactDestinationMessage;
        if (!await HasAccountsAsync(noAccountsMessage, Translator.FileActivation_ImportFailedTitle)) return;

        var paths = new List<string>();
        foreach (var file in files)
        {
            if (file.Path is { Length: > 0 } path) paths.Add(path);
        }

        var scopes = new List<IDisposable>();
        try
        {
            foreach (var file in files) scopes.Add(file.BeginAccess());

            if (mode == WinoApplicationMode.Calendar)
            {
                var composeArgs = await importer.ImportCalendarEventAsync(paths);
                object parameter = composeArgs is null ? new CalendarPageNavigationArgs { RequestDefaultNavigation = true } : composeArgs;

                if (!await host.EnsureShellAsync(WinoApplicationMode.Calendar, parameter)) return;
                if (composeArgs is null)
                    dialogs.InfoBarMessage(Translator.FileActivation_ImportFailedTitle, Translator.FileActivation_CalendarImportFailedMessage, InfoBarMessageType.Warning);
            }
            else
            {
                var draft = await importer.ImportContactAsync(paths);

                if (!await host.EnsureShellAsync(WinoApplicationMode.Contacts, draft is null ? null : new ContactEditNavigationParameter(ImportDraft: draft))) return;
                if (draft is null)
                    dialogs.InfoBarMessage(Translator.FileActivation_ImportFailedTitle, Translator.FileActivation_ContactImportFailedMessage, InfoBarMessageType.Warning);
            }
        }
        finally
        {
            foreach (var scope in scopes) scope.Dispose();
        }
    }

    /// <summary>
    /// Windows App.HandleShareTargetActivationAsync: the files become the pending share request, which the
    /// mail shell turns into a new mail (asking for the account when there are several). A cold or
    /// mode-switching shell consumes it while it activates; a running mail shell is asked directly.
    /// </summary>
    private async Task HandleAttachmentFilesAsync(IReadOnlyList<IActivationFile> files)
    {
        if (!await HasAccountsAsync(Translator.DialogMessage_NoAccountsForCreateMailMessage, Translator.DialogMessage_NoAccountsForCreateMailTitle)) return;

        var shared = new List<SharedFile>();
        foreach (var file in files)
        {
            if (file.Path is not { } path || Directory.Exists(path)) continue;

            var bytes = await ReadAsync(file, path, "Could not read a file to attach.");
            if (bytes is not null) shared.Add(new SharedFile(path, bytes));
        }

        if (shared.Count == 0) return;

        var mailReady = host.IsShellActive(WinoApplicationMode.Mail);
        activationState.PendingShareRequest = new MailShareRequest(shared);

        if (!await host.EnsureShellAsync(WinoApplicationMode.Mail)) return;
        if (mailReady)
        {
            var shell = services.GetRequiredService<MailAppShellViewModel>();
            await dispatcher.ExecuteOnUIThread(() => _ = HandlePendingShareAsync(shell));
        }
    }

    private async Task HandlePendingShareAsync(MailAppShellViewModel shell)
    {
        try
        {
            await shell.HandlePendingShareRequestAsync();
        }
        catch (Exception error)
        {
            _logger.Error(error, "Could not create a mail with the shared files.");
        }
    }

    /// <summary>
    /// Opened .eml files: each one read-only in its own reader. No account is needed. The file is read
    /// inside its access scope, which ends before the reader renders from memory.
    /// </summary>
    private async Task HandleMailFilesAsync(IReadOnlyList<IActivationFile> files)
    {
        var distinct = new List<(IActivationFile File, string Path)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file.Path is { Length: > 0 } path && seen.Add(path)) distinct.Add((file, path));
        }

        if (distinct.Count > MaxMailFileWindows)
            _logger.Information("Opening the first {Count} of {Total} message files.", MaxMailFileWindows, distinct.Count);

        foreach (var (file, path) in distinct.Take(MaxMailFileWindows))
        {
            var bytes = await ReadAsync(file, path, "Could not read an opened message file.");

            var opened = false;
            if (bytes is { Length: > 0 })
            {
                try
                {
                    opened = await host.OpenMailFileAsync(path, bytes);
                }
                catch (Exception error)
                {
                    _logger.Error(error, "Could not open a message window.");
                }
            }

            if (!opened)
                await dialogs.ShowMessageAsync(string.Format(Translator.MacPlatform_EmlOpenFailedMessage, Path.GetFileName(path)),
                    Translator.MacPlatform_EmlOpenFailedTitle, WinoCustomMessageDialogIcon.Warning);
        }
    }

    private async Task<byte[]?> ReadAsync(IActivationFile file, string path, string failureMessage)
    {
        using var scope = file.BeginAccess();
        try
        {
            return await File.ReadAllBytesAsync(path);
        }
        catch (Exception error)
        {
            _logger.Warning(error, failureMessage);
            return null;
        }
    }

    /// <summary>Activations that need an account show a message and are dropped when none exists.</summary>
    private async Task<bool> HasAccountsAsync(string message, string title)
    {
        var accounts = await accountService.GetAccountsAsync();
        if (accounts.Count > 0) return true;

        await dialogs.ShowMessageAsync(message, title, WinoCustomMessageDialogIcon.Warning);
        return false;
    }
}
