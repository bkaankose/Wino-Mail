using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Launch;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Calendar.ViewModels;
using Wino.Messaging.UI;
using Wino.Platform.MacOS.Services;

namespace Wino.Mail.MacOS;

/// <summary>
/// The Dock menu (the Windows Jump List): New Mail, New Event and the folders the user added to
/// quick folders. The badge itself is set by <see cref="MacNotificationBuilder"/> (mail unread only).
/// </summary>
public sealed partial class AppDelegate
{
    private MacNotificationBuilder? DockBuilder => _services?.GetService<INotificationBuilder>() as MacNotificationBuilder;

    partial void DockServicesReady()
    {
        if (_services is null) return;
        // Account switches and renames change the folder list (App.Receive(AccountUpdatedMessage) on Windows).
        WeakReferenceMessenger.Default.Register<AppDelegate, AccountUpdatedMessage>(this, (r, _) => r.RefreshDockMenu());
        WeakReferenceMessenger.Default.Register<AppDelegate, AccountCreatedMessage>(this, (r, _) => r.RefreshDockMenu());
        WeakReferenceMessenger.Default.Register<AppDelegate, AccountRemovedMessage>(this, (r, _) => r.RefreshDockMenu());
        RefreshDockMenu();
#if DEBUG
        MacDebugBridge.Register("dockmenu", async _ =>
        {
            if (DockBuilder is { } builder) await builder.UpdateJumpListOptionsAsync();
            string titles = string.Empty;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                using var menu = ApplicationDockMenu(NSApplication.SharedApplication);
                titles = string.Join(" | ", menu.Items.Select(item => item.IsSeparatorItem ? "—" : item.Title));
            });
            return titles;
        });
#endif
    }

    private void RefreshDockMenu()
    {
        if (DockBuilder is { } builder) Observe(builder.UpdateJumpListOptionsAsync());
    }

    public override NSMenu ApplicationDockMenu(NSApplication sender)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        if (_services is null || !_runtimeStarted || _terminating) return menu;

        menu.AddItem(new NSMenuItem(Translator.MenuNewMail, (_, _) => Observe(NewMailFromDockAsync())));
        menu.AddItem(new NSMenuItem(Translator.CalendarEventCompose_NewEventButton, (_, _) => Observe(NewEventFromDockAsync())));

        var folders = DockBuilder?.DockFolders ?? Array.Empty<MacDockFolderEntry>();
        if (folders.Count > 0)
        {
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(new NSMenuItem(Translator.JumpList_QuickFoldersGroup) { Enabled = false });
            foreach (var folder in folders)
            {
                var entry = folder;
                menu.AddItem(new NSMenuItem(entry.Title, (_, _) => Observe(OpenFolderFromDockAsync(entry))));
            }
        }

        // The menu is built synchronously from the last model; refresh it for the next time.
        RefreshDockMenu();
        return menu;
    }

    private async Task NewMailFromDockAsync()
    {
        var navigation = _services!.GetRequiredService<AppKitNavigationService>();
        if (!await navigation.EnsureShellAsync(WinoApplicationMode.Mail)) return;
        await _dispatcher.ExecuteOnUIThread(() => navigation.Shell?.NewItem());
    }

    private async Task NewEventFromDockAsync()
    {
        var navigation = _services!.GetRequiredService<AppKitNavigationService>();
        if (!await navigation.EnsureShellAsync(WinoApplicationMode.Calendar)) return;
        var calendar = _services!.GetRequiredService<CalendarAppShellViewModel>();
        Task command = Task.CompletedTask;
        await _dispatcher.ExecuteOnUIThread(() => command = calendar.NewEventCommand.ExecuteAsync(null));
        await command;
    }

    /// <summary>Opens a quick folder through the mail shell's folder launch request.</summary>
    private Task OpenFolderFromDockAsync(MacDockFolderEntry entry)
        => _services!.GetRequiredService<AppKitNavigationService>()
            .EnsureShellAsync(WinoApplicationMode.Mail, new MailFolderLaunchRequest(entry.AccountId, entry.FolderId));
}
