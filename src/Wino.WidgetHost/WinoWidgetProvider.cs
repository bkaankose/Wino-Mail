using System.Text.Json;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.NotificationHost;
using Wino.NotificationHost.Contracts;

namespace Wino.WidgetHost;

/// <summary>
/// Widgets Board callbacks arrive on MTA threads. Every callback swallows its exceptions so a widget failure stays
/// inside this process. The host keeps no data of its own: it renders the snapshot the main application writes and
/// forwards actions to it.
/// </summary>
internal sealed partial class WinoWidgetProvider : IWidgetProvider, IDisposable
{
    private static readonly TimeSpan IdleExitDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SnapshotSettleDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MinuteRefresh = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, WidgetState> _widgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _templates = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim _exitSignal = new(false);
    private readonly string _localCachePath = ApplicationData.Current.LocalCacheFolder.Path;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _snapshotTimer;
    private readonly Timer _minuteTimer;
    private DateTimeOffset _idleSince = DateTimeOffset.UtcNow;
    private WidgetSnapshot? _lastSnapshot;
    private readonly Dictionary<string, WidgetCard> _sentCards = new(StringComparer.Ordinal);

    public WinoWidgetProvider()
    {
        _snapshotTimer = new Timer(static state => ((WinoWidgetProvider)state!).RefreshActive(), this, Timeout.Infinite, Timeout.Infinite);
        _minuteTimer = new Timer(static state => ((WinoWidgetProvider)state!).RefreshActive(), this, MinuteRefresh, MinuteRefresh);

        try
        {
            foreach (var info in WidgetManager.GetDefault().GetWidgetInfos() ?? [])
            {
                var context = info.WidgetContext;
                lock (_gate)
                    _widgets[context.Id] = new WidgetState(context.DefinitionId, context.Size, context.IsActive);
            }
        }
        catch
        {
        }

        try
        {
            var directory = WidgetSnapshotStore.GetDirectory(_localCachePath);
            Directory.CreateDirectory(directory);
            _watcher = new FileSystemWatcher(directory, WidgetSnapshotStore.FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            _watcher.Changed += (_, _) => ScheduleSnapshotRefresh();
            _watcher.Created += (_, _) => ScheduleSnapshotRefresh();
            _watcher.Renamed += (_, _) => ScheduleSnapshotRefresh();
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
        }
    }

    /// <summary>
    /// Returns once no Wino widget has been active for a minute. The Board activates the provider again through COM
    /// when it next shows a Wino widget, and it keeps showing the last card meanwhile.
    /// </summary>
    public void WaitUntilIdle()
    {
        while (!_exitSignal.Wait(TimeSpan.FromSeconds(5)))
        {
            lock (_gate)
            {
                if (_widgets.Values.Any(static widget => widget.IsActive))
                {
                    _idleSince = DateTimeOffset.UtcNow;
                    continue;
                }

                if (DateTimeOffset.UtcNow - _idleSince >= IdleExitDelay)
                    return;
            }
        }
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        try
        {
            Track(widgetContext);
            Update(widgetContext.Id);
        }
        catch
        {
        }
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        try
        {
            lock (_gate)
            {
                _widgets.Remove(widgetId);
                _sentCards.Remove(widgetId);
            }
        }
        catch
        {
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        try
        {
            MarkActivity();
            var verb = actionInvokedArgs.Verb;
            if (!WidgetActions.IsKnown(verb))
                return;

            Forward(verb, ReadValue(actionInvokedArgs.Data));
        }
        catch
        {
        }
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        try
        {
            Track(contextChangedArgs.WidgetContext);
            Update(contextChangedArgs.WidgetContext.Id);
        }
        catch
        {
        }
    }

    public void Activate(WidgetContext widgetContext)
    {
        try
        {
            Track(widgetContext, isActive: true);
            Update(widgetContext.Id);
        }
        catch
        {
        }
    }

    public void Deactivate(string widgetId)
    {
        try
        {
            lock (_gate)
            {
                if (_widgets.TryGetValue(widgetId, out var widget))
                    _widgets[widgetId] = widget with { IsActive = false };
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _snapshotTimer.Dispose();
        _minuteTimer.Dispose();
        _exitSignal.Set();
    }

    private void Track(WidgetContext context, bool? isActive = null)
    {
        lock (_gate)
        {
            _widgets[context.Id] = new WidgetState(context.DefinitionId, context.Size, isActive ?? context.IsActive);
            _idleSince = DateTimeOffset.UtcNow;
        }
    }

    private void ScheduleSnapshotRefresh() => _snapshotTimer.Change(SnapshotSettleDelay, Timeout.InfiniteTimeSpan);

    private void RefreshActive()
    {
        try
        {
            List<string> active;
            lock (_gate)
                active = _widgets.Where(static pair => pair.Value.IsActive).Select(static pair => pair.Key).ToList();

            if (active.Count == 0)
                return;

            var snapshot = ReadSnapshot();
            foreach (var widgetId in active)
                Update(widgetId, snapshot);
        }
        catch
        {
        }
    }

    private void Update(string widgetId) => Update(widgetId, ReadSnapshot());

    /// <summary>A failed read keeps the last good snapshot rather than blanking the widgets.</summary>
    private WidgetSnapshot? ReadSnapshot()
    {
        var snapshot = WidgetSnapshotStore.TryRead(_localCachePath);
        lock (_gate)
        {
            if (snapshot != null)
                _lastSnapshot = snapshot;

            return _lastSnapshot;
        }
    }

    /// <summary>Any call from the Board counts as activity, so the host never exits right after being activated.</summary>
    private void MarkActivity()
    {
        lock (_gate)
            _idleSince = DateTimeOffset.UtcNow;
    }

    private void Update(string widgetId, WidgetSnapshot? snapshot)
    {
        WidgetState widget;
        lock (_gate)
        {
            if (!_widgets.TryGetValue(widgetId, out widget))
                return;
        }

        if (!WidgetRenderer.IsKnownDefinition(widget.DefinitionId))
            return;

        var card = WidgetRenderer.Render(widget.DefinitionId, widget.Size, snapshot, DateTimeOffset.Now);

        // The minute refresh and every snapshot write render all active widgets; only cards that changed are sent.
        lock (_gate)
        {
            if (_sentCards.TryGetValue(widgetId, out var sent) && sent == card)
                return;
        }

        WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(widgetId)
        {
            Template = GetTemplate(card.TemplateName),
            Data = card.Data
        });

        lock (_gate)
            _sentCards[widgetId] = card;
    }

    private string GetTemplate(string name)
    {
        lock (_gate)
        {
            if (_templates.TryGetValue(name, out var cached))
                return cached;
        }

        using var stream = typeof(WinoWidgetProvider).Assembly.GetManifestResourceStream($"Wino.WidgetHost.Templates.{name}.json")
            ?? throw new InvalidOperationException($"Missing widget template {name}.");
        using var reader = new StreamReader(stream);
        var template = reader.ReadToEnd();

        lock (_gate)
            _templates[name] = template;

        return template;
    }

    private static string? ReadValue(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;

        using var document = JsonDocument.Parse(data);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.TryGetProperty("value", out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// Hands the action to the main application the way a toast activation is handed over: an envelope in the local
    /// cache and a packaged activation of the Mail entry that names it.
    /// </summary>
    private void Forward(string verb, string? value)
    {
        var activationId = Guid.NewGuid();
        var activation = new NotificationHostActivation(
            DateTimeOffset.UtcNow,
            NotificationHostApplication.Mail,
            WidgetActions.CreateArgument(verb, value),
            new Dictionary<string, string>());

        NotificationHostFileStore.WriteActivationAsync(_localCachePath, activationId, activation).GetAwaiter().GetResult();

        try
        {
            _ = PackagedApplicationActivator.Activate(
                $"{Package.Current.Id.FamilyName}!{NotificationHostApplicationIds.Main}",
                NotificationHostLaunchArguments.CreateForwardedActivation(activationId));
        }
        catch
        {
            NotificationHostFileStore.TryDeleteActivation(_localCachePath, activationId);
            throw;
        }
    }

    private readonly record struct WidgetState(string DefinitionId, WidgetSize Size, bool IsActive);
}
