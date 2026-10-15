using Android.Content;
using Android.Views;
using CommunityToolkit.Mvvm.Messaging;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.SideSheet;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels;
using Wino.Messaging.UI;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Extras;

/// <summary>
/// The Daily briefing on Android (Windows ShellWindow title-bar button and DailyBriefingPanel; iOS DailyBriefingPresenter):
/// tracks whether the shell offers the briefing through the shared <see cref="DailyBriefingAvailability"/> rule (the
/// signed-in Wino account can use Intelligence surfaces) and whether it has unseen content, and shows the one
/// <see cref="DailyBriefingPanelView"/> over the shared <see cref="DailyBriefingPanelViewModel"/> in a modal bottom sheet
/// on compact widths and a modal side sheet on medium and expanded widths (canvas Material pass board Briefing).
/// </summary>
public sealed class AndroidDailyBriefingPresenter :
    IRecipient<DailyBriefingStateChanged>,
    IRecipient<IntelligenceMetadataChanged>,
    IRecipient<WinoIntelligenceAccessChanged>,
    IRecipient<WinoIntelligenceEntitlementChanged>,
    IRecipient<WinoAccountProfileUpdatedMessage>,
    IRecipient<WinoAccountProfileDeletedMessage>
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly ILocalIntelligenceService _localIntelligence;
    private readonly IWinoAccountIntelligenceSnapshotService _entitlements;
    private DailyBriefingPanelViewModel? _viewModel;
    private global::Android.App.Dialog? _sheet;
    private DailyBriefingShellState _state = DailyBriefingShellState.Unavailable;

    public AndroidDailyBriefingPresenter(IServiceProvider services, IDispatcher dispatcher,
        ILocalIntelligenceService localIntelligence, IWinoAccountIntelligenceSnapshotService entitlements)
    {
        _services = services;
        _dispatcher = dispatcher;
        _localIntelligence = localIntelligence;
        _entitlements = entitlements;
        // One registration per message (trim-safe).
        var messenger = WeakReferenceMessenger.Default;
        messenger.Register<DailyBriefingStateChanged>(this);
        messenger.Register<IntelligenceMetadataChanged>(this);
        messenger.Register<WinoIntelligenceAccessChanged>(this);
        messenger.Register<WinoIntelligenceEntitlementChanged>(this);
        messenger.Register<WinoAccountProfileUpdatedMessage>(this);
        messenger.Register<WinoAccountProfileDeletedMessage>(this);
        _ = RefreshStateAsync(refreshEntitlement: true);
    }

    /// <summary>True while the briefing can be shown at all (Windows hides the title-bar button otherwise).</summary>
    public bool IsAvailable => _state.IsAvailable;

    /// <summary>True when the briefing has unseen items (the badge on the app bar action).</summary>
    public bool HasUnseenItems => _state.HasUnseenItems;

    /// <summary>Raised on the UI thread when <see cref="IsAvailable"/> or <see cref="HasUnseenItems"/> changes.</summary>
    public event EventHandler? StateChanged;

    public bool IsOpen => _sheet?.IsShowing == true;

    /// <summary>Shows the briefing: a bottom sheet on compact widths, a side sheet otherwise. Main thread.</summary>
    public void Show(Context context, bool compact) => Present(context, compact, requireAccess: true);

    /// <summary>Closes the sheet (a mode switch, the host page going away).</summary>
    public void Close()
    {
        var sheet = _sheet;
        _sheet = null;
        if (sheet?.IsShowing == true) sheet.Dismiss();
    }

#if DEBUG
    /// <summary>Debug only: opens the sheet without the entitlement so its layout can be checked on the emulator.</summary>
    internal DailyBriefingPanelViewModel ShowForLayoutCheck(Context context, bool compact)
    {
        Present(context, compact, requireAccess: false);
        return ViewModel();
    }
#endif

    private DailyBriefingPanelViewModel ViewModel() => _viewModel ??= _services.GetRequiredService<DailyBriefingPanelViewModel>();

    private void Present(Context context, bool compact, bool requireAccess)
    {
        if (requireAccess && !IsAvailable) return;
        Close();
        var viewModel = ViewModel();
        var panel = new DailyBriefingPanelView(context, viewModel, _dispatcher, showHandle: compact);
        global::Android.App.Dialog sheet;
        if (compact)
        {
            var bottom = new BottomSheetDialog(context);
            int screen = context.Resources?.DisplayMetrics?.HeightPixels ?? 0;
            int height = (int)(screen * WinoColors.Fraction(context, Resource.Fraction.wino_m3_briefing_sheet_screen_fraction));
            bottom.SetContentView(panel, new global::Android.Widget.FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, height));
            bottom.Behavior.PeekHeight = height;
            bottom.Behavior.SkipCollapsed = true;
            bottom.Behavior.State = BottomSheetBehavior.StateExpanded;
            sheet = bottom;
        }
        else
        {
            var side = new SideSheetDialog(context);
            int width = WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_side_width);
            side.SetContentView(panel, new global::Android.Widget.FrameLayout.LayoutParams(width, ViewGroup.LayoutParams.MatchParent));
            // The Material side sheet container is 256 dp by default; the briefing uses the 400 dp Windows panel width.
            if (side.FindViewById(Resource.Id.m3_side_sheet) is { LayoutParameters: { } container } sheetView)
            {
                container.Width = width;
                sheetView.LayoutParameters = container;
            }
            sheet = side;
        }
        panel.CloseRequested += (_, _) => sheet.Dismiss();
        sheet.DismissEvent += (_, _) =>
        {
            panel.Detach();
            if (ReferenceEquals(_sheet, sheet)) _sheet = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        _sheet = sheet;
        sheet.Show();
        panel.Attach();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Receive(DailyBriefingStateChanged message) => _ = RefreshStateAsync();
    public void Receive(IntelligenceMetadataChanged message) => _ = RefreshStateAsync();
    public void Receive(WinoIntelligenceAccessChanged message) => _ = RefreshStateAsync();
    public void Receive(WinoAccountProfileUpdatedMessage message) => _ = RefreshStateAsync();
    public void Receive(WinoAccountProfileDeletedMessage message) => _ = RefreshStateAsync();

    public void Receive(WinoIntelligenceEntitlementChanged message)
    {
        if (message.Entitlement.CanAccessSurfaces) _ = RefreshStateAsync();
        else _ = Publish(DailyBriefingShellState.Unavailable);
    }

    private async Task RefreshStateAsync(bool refreshEntitlement = false)
    {
        try
        {
            if (refreshEntitlement)
            {
                try { await _entitlements.RefreshEntitlementAsync().ConfigureAwait(false); }
                catch (Exception exception) { Serilog.Log.Warning(exception, "The Wino account entitlement could not be refreshed for the Daily briefing."); }
            }
            await Publish(await DailyBriefingAvailability.ReadAsync(_entitlements, _localIntelligence).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Failed to refresh the Daily briefing state.");
            await Publish(DailyBriefingShellState.Unavailable).ConfigureAwait(false);
        }
    }

    private Task Publish(DailyBriefingShellState state) => _dispatcher.ExecuteOnUIThread(() =>
    {
        bool changed = state != _state;
        _state = state;
        // Losing access closes an open briefing (Windows ShellWindow).
        if (!state.IsAvailable) Close();
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    });
}
