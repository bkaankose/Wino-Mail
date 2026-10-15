using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Messaging.UI;

namespace Wino.Mail.UIKit.Views.Extras;

/// <summary>
/// Supplies the Daily briefing panel and tracks the badge state (Mac DailyBriefingPresenter, Windows ShellWindow): the
/// briefing is unseen only while the signed-in Wino account can use Intelligence surfaces, at least one account is
/// eligible and the local store has unseen content. The panel controller is created once and reused by the iPad
/// overlay and the iPhone sheet.
/// </summary>
public sealed class DailyBriefingPresenter : IUIKitDailyBriefingPresenter,
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
    private readonly IUIKitShellHost _host;
    private DailyBriefingPanelViewController? _panel;
    private Action? _close;
    private bool _hasUnseenItems;
    private bool _hasAccess;

    public DailyBriefingPresenter(IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger,
        ILocalIntelligenceService localIntelligence, IWinoAccountIntelligenceSnapshotService entitlements, IUIKitShellHost host)
    {
        _services = services;
        _dispatcher = dispatcher;
        _logger = logger;
        _localIntelligence = localIntelligence;
        _entitlements = entitlements;
        _host = host;
        // One registration per message (trim-safe; RegisterAll needs reflection when trimmed).
        var messenger = WeakReferenceMessenger.Default;
        messenger.Register<DailyBriefingStateChanged>(this);
        messenger.Register<IntelligenceMetadataChanged>(this);
        messenger.Register<WinoIntelligenceAccessChanged>(this);
        messenger.Register<WinoIntelligenceEntitlementChanged>(this);
        messenger.Register<WinoAccountProfileUpdatedMessage>(this);
        messenger.Register<WinoAccountProfileDeletedMessage>(this);
        _ = RefreshStateAsync(refreshEntitlement: true);
    }

    public bool HasUnseenItems => _hasUnseenItems;

    public bool IsAvailable => _hasAccess;

    public event EventHandler? UnseenItemsChanged;

    /// <summary>The panel once created; null until a shell asked for it.</summary>
    public DailyBriefingPanelViewController? Panel => _panel;

    public DailyBriefingPanelViewController CreatePanel(Action close)
    {
        // The panel outlives a shell (the last account removed, then a new one added): route its close button to the
        // shell that asked for it most recently.
        _close = close;
        if (_panel is not null) return _panel;
        var viewModel = _services.GetRequiredService<DailyBriefingPanelViewModel>();
        _panel = new DailyBriefingPanelViewController(viewModel, _dispatcher, _logger, () => _close?.Invoke());
        return _panel;
    }

    public void PresentSheet()
    {
        if (_host.TopViewController is not { } presenter) return;
        var panel = CreatePanel(() =>
        {
            if (_panel is { PresentingViewController: not null } shown) shown.DismissViewController(true, null);
        });
        // Already on screen (as the overlay or this sheet): nothing to present.
        if (panel.ParentViewController is not null || panel.PresentingViewController is not null) return;
        panel.ModalPresentationStyle = UIModalPresentationStyle.PageSheet;
        if (panel.SheetPresentationController is { } sheet)
        {
            sheet.Detents = [UISheetPresentationControllerDetent.CreateMediumDetent(), UISheetPresentationControllerDetent.CreateLargeDetent()];
            sheet.PrefersGrabberVisible = true;
            sheet.PreferredCornerRadius = (nfloat)Wino.Presentation.UIKit.WinoStyle.ZoneRadius * 2;
        }
        presenter.PresentViewController(panel, true, null);
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
}
