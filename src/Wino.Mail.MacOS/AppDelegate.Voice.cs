using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Infrastructure.Voice;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Voice;
using Wino.Messaging.UI;

namespace Wino.Mail.MacOS;

/// <summary>
/// Voice control (Windows App.Voice.cs and VoiceBubbleService): the floating voice bubble with its global shortcut,
/// run by the shared <see cref="VoiceBubbleController"/>, and the plan steps that end in Wino's own windows.
/// Nothing here opens the microphone; the controller does that only when the user starts a command.
/// </summary>
public sealed partial class AppDelegate
{
    private VoiceAssistantActivation? _voiceActivation;
    private MacVoiceBubbleHost? _voiceHost;
    private VoiceBubbleController? _voiceController;

    /// <summary>UI thread, after the menu bar icon exists, so the bubble can open under it.</summary>
    private void StartVoice()
    {
        if (_services is not { } services || _voiceActivation is not null)
            return;

        _voiceActivation = new VoiceAssistantActivation(
            services,
            action =>
            {
                if (NSThread.IsMain)
                    action();
                else
                    NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
            },
            CreateVoice,
            DisposeVoice);
        _voiceActivation.Start();

#if DEBUG
        RegisterVoiceDebugCommands();
#endif
    }

    private void CreateVoice()
    {
        if (_services is not { } services || _voiceHost is not null)
            return;

        try
        {
            _voiceHost = new MacVoiceBubbleHost(() => _statusItem?.ButtonScreenFrame, services.GetRequiredService<IPreferencesService>());
            _voiceController = new VoiceBubbleController(_voiceHost, new MacVoicePlanHost(this), services);
            services.GetRequiredService<MacVoiceHotKeyAccess>().Attach(_voiceController, _voiceHost);
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Failed to create the voice bubble. Continuing without it.");
            DisposeVoice();
        }
    }

    private void DisposeVoice()
    {
        _services?.GetService<MacVoiceHotKeyAccess>()?.Attach(null, null);
        try
        {
            _voiceController?.Dispose();
            _voiceHost?.Dispose();
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Voice bubble cleanup failed.");
        }

        _voiceController = null;
        _voiceHost = null;
    }

    /// <summary>UI thread, while quitting.</summary>
    private void StopVoice()
    {
        _voiceActivation?.Dispose();
        _voiceActivation = null;
    }

    private async Task OpenVoiceSearchAsync(MailAccount account, IReadOnlyList<IMailItemFolder> folders, MailSearchCriteria criteria)
    {
        await ShowWinoAsync(WinoApplicationMode.Mail);
        var shell = _services!.GetRequiredService<MailAppShellViewModel>();
        Task? navigated = null;
        await _dispatcher.ExecuteOnUIThread(() => navigated = NavigateVoiceSearchFolderAsync(shell, account, folders));
        if (navigated is not null)
            await navigated;

        WeakReferenceMessenger.Default.Send(new MailSearchRequested(criteria, folders));
    }

    /// <summary>The list loads the folder before it can search it; a search sent earlier would be replaced by the load.</summary>
    private static async Task NavigateVoiceSearchFolderAsync(MailAppShellViewModel shell, MailAccount account, IReadOnlyList<IMailItemFolder> folders)
    {
        if (shell.MenuItems.TryGetAccountMenuItem(account.Id, out IAccountMenuItem accountMenuItem))
            await shell.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);

        var folderMenuItem = folders
            .Select(folder => shell.MenuItems.TryGetFolderMenuItem(folder.Id, out IBaseFolderMenuItem item) ? item : null)
            .FirstOrDefault(static item => item is not null);
        if (folderMenuItem is null && shell.MenuItems.TryGetSpecialFolderMenuItem(account.Id, SpecialFolderType.Inbox, out var inbox))
            folderMenuItem = inbox;

        if (folderMenuItem is null)
            return;

        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await shell.NavigateFolderAsync(folderMenuItem, loaded);
        await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    private async Task OpenVoiceDraftAsync(MailAccount account, MailCopy draft)
    {
        var navigator = _services!.GetRequiredService<INotificationResponseNavigator>();
        if (await navigator.ShowDraftsAsync(account.Id))
            await navigator.OpenComposerAsync(draft);
    }

    private async Task OpenVoiceEventEditorAsync(CalendarEventComposeNavigationArgs args)
    {
        await ShowWinoAsync(WinoApplicationMode.Calendar);
        await _dispatcher.ExecuteOnUIThread(() => _services!.GetRequiredService<AppKitNavigationService>().Navigate(WinoPage.CalendarEventComposePage, args));
    }

    /// <summary>The Mac side of <see cref="IVoicePlanHost"/>: the same paths notifications and the companion use.</summary>
    private sealed class MacVoicePlanHost(AppDelegate app) : IVoicePlanHost
    {
        public Task OpenSearchAsync(MailAccount account, IReadOnlyList<IMailItemFolder> folders, MailSearchCriteria criteria)
            => app.OpenVoiceSearchAsync(account, folders, criteria);

        public Task OpenMailAsync(MailCopy mail) => app._notificationResponses.NavigateMailAsync(mail.UniqueId);

        public Task OpenDraftAsync(MailAccount account, MailCopy draft) => app.OpenVoiceDraftAsync(account, draft);

        public Task OpenEventAsync(CalendarItem calendarItem) => app._notificationResponses.NavigateCalendarItemAsync(calendarItem.Id);

        public Task OpenEventEditorAsync(CalendarEventComposeNavigationArgs args) => app.OpenVoiceEventEditorAsync(args);
    }
}
