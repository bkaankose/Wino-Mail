using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Messaging.UI;

namespace Wino.Mail.MacOS.Views.Extras;

/// <summary>
/// Supplies the Daily briefing panel to the shell and tracks the title-bar badge state the way the
/// Windows ShellWindow does: the briefing is unseen only while the signed-in Wino account can use
/// Intelligence surfaces, at least one account is eligible and the local store has unseen content.
/// </summary>
public sealed class DailyBriefingPresenter : IDailyBriefingPresenter,
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
    private DailyBriefingPanelViewController? _panel;
    private Action? _close;
    private bool _hasUnseenItems;
    private bool _hasAccess;

    public DailyBriefingPresenter(IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger,
        ILocalIntelligenceService localIntelligence, IWinoAccountIntelligenceSnapshotService entitlements)
    {
        _services = services;
        _dispatcher = dispatcher;
        _logger = logger;
        _localIntelligence = localIntelligence;
        _entitlements = entitlements;
        WeakReferenceMessenger.Default.RegisterAll(this);
        _ = RefreshStateAsync(refreshEntitlement: true);
#if DEBUG
        ShellExtrasDebug.Services ??= services;
#endif
    }

    public bool HasUnseenItems => _hasUnseenItems;

    /// <summary>True when the briefing can be shown at all (Windows hides the title-bar button otherwise).</summary>
    public bool IsAvailable => _hasAccess;

    public event EventHandler? UnseenItemsChanged;

    /// <summary>The panel once created; null until the shell asked for it.</summary>
    public DailyBriefingPanelViewController? Panel => _panel;

    public NSViewController CreatePanel(Action close)
    {
        // The panel outlives a shell (the last account removed, then a new one added): route its
        // close button to the shell that asked for it most recently.
        _close = close;
        if (_panel is not null) return _panel;
        var viewModel = _services.GetRequiredService<DailyBriefingPanelViewModel>();
        _panel = new DailyBriefingPanelViewController(viewModel, _dispatcher, _logger, () => _close?.Invoke());
        return _panel;
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
            var entitlement = await _entitlements.GetEntitlementAsync().ConfigureAwait(false);
            var hasAccess = entitlement.CanAccessSurfaces;
            var eligible = hasAccess ? await _localIntelligence.GetEligibleAccountsAsync().ConfigureAwait(false) : [];
            var unseen = eligible.Count > 0
                ? await _localIntelligence.GetUnseenStateAsync().ConfigureAwait(false)
                : new DailyBriefingUnseenState(false, null);
            await Publish(hasAccess, unseen.HasUnseenContent).ConfigureAwait(false);
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
}
