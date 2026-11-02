using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.ViewModels.Companion;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// Publishes the Home Screen quick actions: New Mail, New Event and New Task while an account exists, plus the next
/// timed event of today. The system shows the last published set until the app publishes again, so the event time is
/// absolute and the set is refreshed when the scene goes to the background and after a background sync.
/// </summary>
public sealed class UIKitQuickActionItems(
    IAccountService accounts,
    CompanionContentService companion,
    IPreferencesService preferences,
    IDispatcher dispatcher)
{
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _pendingRefresh;
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        var messenger = WeakReferenceMessenger.Default;
        messenger.Register<UIKitQuickActionItems, AccountCreatedMessage>(this, static (r, _) => r.RequestRefresh());
        messenger.Register<UIKitQuickActionItems, AccountRemovedMessage>(this, static (r, _) => r.RequestRefresh());
        messenger.Register<UIKitQuickActionItems, AccountUpdatedMessage>(this, static (r, _) => r.RequestRefresh());
        messenger.Register<UIKitQuickActionItems, CalendarItemAdded>(this, static (r, _) => r.RequestRefresh());
        messenger.Register<UIKitQuickActionItems, CalendarItemUpdated>(this, static (r, _) => r.RequestRefresh());
        messenger.Register<UIKitQuickActionItems, CalendarItemDeleted>(this, static (r, _) => r.RequestRefresh());
        messenger.Register<UIKitQuickActionItems, CalendarItemOperationsCompleted>(this, static (r, _) => r.RequestRefresh());
        messenger.Register<UIKitQuickActionItems, CalendarListRefreshed>(this, static (r, _) => r.RequestRefresh());
        messenger.Register<UIKitQuickActionItems, CalendarListUpdated>(this, static (r, _) => r.RequestRefresh());
        messenger.Register<UIKitQuickActionItems, CalendarListDeleted>(this, static (r, _) => r.RequestRefresh());
        messenger.Register<UIKitQuickActionItems, AccountCalendarSynchronizationStateChanged>(this, static (r, m) =>
        {
            if (!m.IsSynchronizationInProgress) r.RequestRefresh();
        });
        messenger.Register<UIKitQuickActionItems, LanguageChanged>(this, static (r, _) => r.RequestRefresh());
        messenger.Register<UIKitQuickActionItems, PreferenceChangedMessage>(this, static (r, m) =>
        {
            if (m.PropertyName is nameof(IPreferencesService.CalendarTimeFormatPreference) or nameof(IPreferencesService.CurrentLanguage)) r.RequestRefresh();
        });
        RequestRefresh();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        Interlocked.Exchange(ref _pendingRefresh, null)?.Cancel();
    }

    /// <summary>Refreshes shortly, coalescing bursts of calendar and account messages.</summary>
    public void RequestRefresh()
    {
        if (!_started) return;
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _pendingRefresh, cancellation)?.Cancel();
        _ = RefreshLaterAsync(cancellation.Token);
    }

    private async Task RefreshLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RefreshDelay, cancellationToken);
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_started) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = await BuildAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await dispatcher.ExecuteOnUIThread(() => UIApplication.SharedApplication.ShortcutItems = [.. entries.Select(static entry => entry.Create())]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Could not refresh the Home Screen quick actions."); }
        finally { _gate.Release(); }
    }

    private async Task<List<QuickActionEntry>> BuildAsync(CancellationToken cancellationToken)
    {
        var all = await accounts.GetAccountsAsync();
        if (all.Count == 0) return [];

        var items = new List<QuickActionEntry>
        {
            new(QuickActionIdentifiers.NewMail, Translator.QuickAction_NewMail, null, "square.and.pencil", null),
            new(QuickActionIdentifiers.NewEvent, Translator.QuickAction_NewEvent, null, "calendar.badge.plus", null),
            new(QuickActionIdentifiers.NewTask, Translator.QuickAction_NewTask, null, "checklist", null),
        };

        var calendarAccounts = all.Where(static account => account.IsCalendarAccessGranted).ToList();
        if (calendarAccounts.Count > 0)
        {
            var upcoming = await companion.GetUpcomingEventsAsync(calendarAccounts, DateTime.Now.Date.AddDays(1), 3, cancellationToken);
            if (upcoming.FirstOrDefault(static item => !item.IsCancelled) is { } next)
            {
                var culture = CultureInfo.DefaultThreadCurrentUICulture ?? CultureInfo.CurrentUICulture;
                var display = DateTimeDisplayFormatter.GetTimeDisplayType(preferences.CalendarTimeFormatPreference, culture);
                var time = string.Format(Translator.Companion_EventTimeFormat,
                    DateTimeDisplayFormatter.FormatTime(next.StartDate, display, culture),
                    DateTimeDisplayFormatter.FormatTime(next.EndDate, display, culture));
                var subtitle = next.StartDate <= DateTime.Now ? $"{Translator.Companion_EventNow} · {time}" : time;
                var title = string.IsNullOrWhiteSpace(next.Title) ? Translator.QuickAction_UntitledEvent : next.Title;
                var symbol = CalendarJoinLinkResolver.TryGetEffectiveJoinUri(next.CalendarItem, out _) ? "video" : "clock";
                items.Add(new(QuickActionIdentifiers.NextMeeting, title, subtitle, symbol, next.Id));
            }
        }
        return items;
    }

    /// <summary>An item computed off the main thread; UIKit creates the shortcut and its icon on the main thread only.</summary>
    private sealed record QuickActionEntry(string Type, string Title, string? Subtitle, string Symbol, Guid? CalendarItemId)
    {
        public UIApplicationShortcutItem Create()
            => new(Type, Title, Subtitle, UIApplicationShortcutIcon.FromSystemImageName(Symbol),
                CalendarItemId is { } id ? QuickActionIdentifiers.CalendarItemPayload(id) : null);
    }

#if DEBUG
    public void RegisterDebugCommands(UIKitQuickActionCoordinator coordinator)
    {
        UIKitDebugBridge.Register("quickactions", async _ =>
        {
            await RefreshAsync();
            var text = string.Empty;
            await dispatcher.ExecuteOnUIThread(() => text = string.Join(" | ", (UIApplication.SharedApplication.ShortcutItems ?? [])
                .Select(item => $"{item.Type} '{item.LocalizedTitle}' '{item.LocalizedSubtitle}' {item.UserInfo?.Description}")));
            return text.Length == 0 ? "(none)" : text;
        });
        UIKitDebugBridge.Register("quickaction", async args =>
        {
            var name = args.FirstOrDefault() ?? string.Empty;
            var type = $"com.winomail.quickaction.{name}";
            UIApplicationShortcutItem? item = null;
            await dispatcher.ExecuteOnUIThread(() => item = (UIApplication.SharedApplication.ShortcutItems ?? []).FirstOrDefault(entry => entry.Type == type));
            if (item is null) return $"no quick action {name}";
            await coordinator.HandleAsync(item);
            return $"ran {name}";
        });
    }
#endif
}
