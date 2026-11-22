using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Linux.Views.Extras;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Supplies the Daily briefing panel to the shell and tracks the header button the way the Windows ShellWindow and the
/// macOS presenter do: the briefing is offered only while the signed-in Wino Account can use Intelligence surfaces, and
/// it is unseen only when an account is eligible and the local store has unseen content (shared
/// <see cref="DailyBriefingAvailability"/>). The panel is created once and kept.
/// </summary>
internal sealed class DailyBriefingPresenter : IDailyBriefingPresenter,
    IRecipient<DailyBriefingStateChanged>,
    IRecipient<IntelligenceMetadataChanged>,
    IRecipient<WinoIntelligenceAccessChanged>,
    IRecipient<WinoIntelligenceEntitlementChanged>,
    IRecipient<WinoAccountProfileUpdatedMessage>,
    IRecipient<WinoAccountProfileDeletedMessage>
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly ILocalIntelligenceService _localIntelligence;
    private readonly IWinoAccountIntelligenceSnapshotService _entitlements;
    private DailyBriefingPanel? _panel;
    private Action? _close;
    private bool _hasUnseenItems;
    private bool _hasAccess;

    public DailyBriefingPresenter(IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger, IMessenger messenger,
        ILocalIntelligenceService localIntelligence, IWinoAccountIntelligenceSnapshotService entitlements)
    {
        _services = services;
        _dispatcher = dispatcher;
        _logger = logger;
        _localIntelligence = localIntelligence;
        _entitlements = entitlements;
        // Typed registrations: RegisterAll reflects over the interfaces, which Native AOT cannot keep.
        messenger.Register<DailyBriefingPresenter, DailyBriefingStateChanged>(this, static (r, m) => r.Receive(m));
        messenger.Register<DailyBriefingPresenter, IntelligenceMetadataChanged>(this, static (r, m) => r.Receive(m));
        messenger.Register<DailyBriefingPresenter, WinoIntelligenceAccessChanged>(this, static (r, m) => r.Receive(m));
        messenger.Register<DailyBriefingPresenter, WinoIntelligenceEntitlementChanged>(this, static (r, m) => r.Receive(m));
        messenger.Register<DailyBriefingPresenter, WinoAccountProfileUpdatedMessage>(this, static (r, m) => r.Receive(m));
        messenger.Register<DailyBriefingPresenter, WinoAccountProfileDeletedMessage>(this, static (r, m) => r.Receive(m));
        _ = RefreshStateAsync(refreshEntitlement: true);
    }

    public bool HasUnseenItems => _hasUnseenItems;

    public bool IsAvailable => _hasAccess
#if DEBUG
        || DebugForceAvailable
#endif
        ;

    public event EventHandler? UnseenItemsChanged;

    /// <summary>The panel once created; null until the shell asked for it.</summary>
    public DailyBriefingPanel? Panel => _panel;

    public Gtk.Widget CreatePanel(Action close)
    {
        // The panel outlives a shell: its close button goes to the shell that asked most recently.
        _close = close;
        if (_panel is { } existing) return existing.Root;
        var viewModel = _services.GetRequiredService<DailyBriefingPanelViewModel>();
        _panel = new DailyBriefingPanel(viewModel, _dispatcher, _logger, () => _close,
            () => _services.GetService<INavigationService>()?.Navigate(WinoPage.MailClassificationPage));
        return _panel.Root;
    }

    public void Receive(DailyBriefingStateChanged message) => _ = RefreshStateAsync();
    public void Receive(IntelligenceMetadataChanged message) => _ = RefreshStateAsync();
    public void Receive(WinoIntelligenceAccessChanged message) => _ = RefreshStateAsync();
    public void Receive(WinoAccountProfileUpdatedMessage message) => _ = RefreshStateAsync();
    public void Receive(WinoAccountProfileDeletedMessage message) => _ = RefreshStateAsync();

    public void Receive(WinoIntelligenceEntitlementChanged message)
    {
        if (message.Entitlement.CanAccessSurfaces)
        {
            _ = RefreshStateAsync();
            return;
        }
        _ = Publish(hasAccess: false, unseen: false);
    }

    private async Task RefreshStateAsync(bool refreshEntitlement = false)
    {
        try
        {
            if (refreshEntitlement)
            {
                try { await _entitlements.RefreshEntitlementAsync().ConfigureAwait(false); }
                catch (Exception exception) { _logger.CaptureException(exception, nameof(DailyBriefingPresenter)); }
            }
            var state = await DailyBriefingAvailability.ReadAsync(_entitlements, _localIntelligence).ConfigureAwait(false);
            await Publish(state.IsAvailable, state.HasUnseenItems).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, nameof(DailyBriefingPresenter));
            await Publish(hasAccess: false, unseen: false).ConfigureAwait(false);
        }
    }

    private Task Publish(bool hasAccess, bool unseen) => _dispatcher.ExecuteOnUIThread(() =>
    {
        var changed = _hasUnseenItems != unseen || _hasAccess != hasAccess;
        _hasAccess = hasAccess;
        _hasUnseenItems = unseen;
        if (changed) UnseenItemsChanged?.Invoke(this, EventArgs.Empty);
    });

#if DEBUG
    /// <summary>Shows the header button without an entitlement, so agents can reach the panel's real unavailable state.</summary>
    internal bool DebugForceAvailable { get; private set; }

    internal void DebugSetForceAvailable(bool on)
    {
        DebugForceAvailable = on;
        UnseenItemsChanged?.Invoke(this, EventArgs.Empty);
    }
#endif
}
