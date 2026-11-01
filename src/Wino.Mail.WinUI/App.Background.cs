using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.WinUI.Services;
using Wino.Mail.WinUI.Services.WidgetsAndTiles;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.UI;

namespace Wino.Mail.WinUI;

/// <summary>
/// Work that follows the app's data while the process runs, with or without a window: a launch from the tray or
/// at sign-in has no shell, so nothing here may depend on one.
/// </summary>
public partial class App :
    IRecipient<MailAddedMessage>,
    IRecipient<MailUpdatedMessage>,
    IRecipient<MailRemovedMessage>,
    IRecipient<BulkMailAddedMessage>,
    IRecipient<BulkMailUpdatedMessage>,
    IRecipient<BulkMailRemovedMessage>,
    IRecipient<MailReadStatusChanged>,
    IRecipient<BulkMailReadStatusChanged>,
    IRecipient<CalendarItemAdded>,
    IRecipient<CalendarItemUpdated>,
    IRecipient<CalendarItemDeleted>,
    IRecipient<TaskStateChanged>,
    IRecipient<TaskSynchronizationCompleted>,
    IRecipient<ContactSynchronizationCompleted>
{
    private WidgetsAndTilesService? _widgetsAndTiles;

    /// <summary>Starts once the launch completes, so the first widget and tile update stays off the startup path.</summary>
    private void StartBackgroundActivities()
    {
        if (_widgetsAndTiles != null ||
            !(WidgetsAndTilesService.AreLiveTilesSupported || WidgetsAndTilesService.AreWidgetsSupported))
            return;

        _widgetsAndTiles = Services.GetRequiredService<WidgetsAndTilesService>();
        Services.GetRequiredService<WindowsShellPreferences>().WidgetsAndTilesPreferencesChanged +=
            (_, _) => _widgetsAndTiles.RequestImmediateUpdate();

        var messenger = WeakReferenceMessenger.Default;
        messenger.Register<MailAddedMessage>(this);
        messenger.Register<MailUpdatedMessage>(this);
        messenger.Register<MailRemovedMessage>(this);
        messenger.Register<BulkMailAddedMessage>(this);
        messenger.Register<BulkMailUpdatedMessage>(this);
        messenger.Register<BulkMailRemovedMessage>(this);
        messenger.Register<MailReadStatusChanged>(this);
        messenger.Register<BulkMailReadStatusChanged>(this);
        messenger.Register<CalendarItemAdded>(this);
        messenger.Register<CalendarItemUpdated>(this);
        messenger.Register<CalendarItemDeleted>(this);
        messenger.Register<TaskStateChanged>(this);
        messenger.Register<TaskSynchronizationCompleted>(this);
        messenger.Register<ContactSynchronizationCompleted>(this);

        _widgetsAndTiles.RequestUpdate();
    }

    /// <summary>Called by every change the widgets and the Live Tile show, including account changes.</summary>
    private void OnBackgroundContentChanged() => _widgetsAndTiles?.RequestUpdate();

    public override void OnLanguageChanged(AppLanguageModel languageModel)
    {
        base.OnLanguageChanged(languageModel);
        OnBackgroundContentChanged();
    }

    public void Receive(MailAddedMessage message) => OnBackgroundContentChanged();
    public void Receive(MailUpdatedMessage message) => OnBackgroundContentChanged();
    public void Receive(MailRemovedMessage message) => OnBackgroundContentChanged();
    public void Receive(BulkMailAddedMessage message) => OnBackgroundContentChanged();
    public void Receive(BulkMailUpdatedMessage message) => OnBackgroundContentChanged();
    public void Receive(BulkMailRemovedMessage message) => OnBackgroundContentChanged();
    public void Receive(MailReadStatusChanged message) => OnBackgroundContentChanged();
    public void Receive(BulkMailReadStatusChanged message) => OnBackgroundContentChanged();
    public void Receive(CalendarItemAdded message) => OnBackgroundContentChanged();
    public void Receive(CalendarItemUpdated message) => OnBackgroundContentChanged();
    public void Receive(CalendarItemDeleted message) => OnBackgroundContentChanged();
    public void Receive(TaskStateChanged message) => OnBackgroundContentChanged();
    public void Receive(TaskSynchronizationCompleted message) => OnBackgroundContentChanged();
    public void Receive(ContactSynchronizationCompleted message) => OnBackgroundContentChanged();
}
