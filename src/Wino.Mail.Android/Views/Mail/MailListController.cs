using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// The part of the message list that lives as long as its routed page rather than one of its views (iOS keeps it in the
/// list controller): the shared projection over the ViewModel's collection, the published selection, and the messages
/// that open and close the reader. On compact widths the list's view is destroyed while the reader covers it, so the
/// reader still closes when its message leaves the list. Disposed when the page is released.
/// </summary>
public sealed class MailListController : IDisposable,
    IRecipient<ActiveMailItemChangedEvent>,
    IRecipient<ClearMailSelectionsRequested>,
    IRecipient<DisposeRenderingFrameRequested>,
    IRecipient<ComposeDetachedDraftRequested>,
    IRecipient<SelectMailItemContainerEvent>,
    IRecipient<WinoIntelligenceEntitlementChanged>
{
    public const string StateKey = "mail-list";

    private readonly IDispatcher _dispatcher;
    private readonly AndroidNavigationService _navigation;
    private readonly IWinoAccountIntelligenceSnapshotService? _entitlements;
    private string _lastPublishedSelection = string.Empty;
    private bool _disposed;

    private MailListController(MailListPageViewModel viewModel, IDispatcher dispatcher, IServiceProvider services)
    {
        ViewModel = viewModel;
        _dispatcher = dispatcher;
        _navigation = services.GetRequiredService<AndroidNavigationService>();
        _entitlements = services.GetService<IWinoAccountIntelligenceSnapshotService>();
        viewModel.MailCollection.CoreDispatcher = dispatcher;
        Projection = new MailListProjection(viewModel.MailCollection.Items, viewModel.MailListOptions ?? new MailListProjectionOptions());
        viewModel.PropertyChanged += ViewModelChanged;
        var messenger = WeakReferenceMessenger.Default;
        messenger.Register<ActiveMailItemChangedEvent>(this);
        messenger.Register<ClearMailSelectionsRequested>(this);
        messenger.Register<DisposeRenderingFrameRequested>(this);
        messenger.Register<ComposeDetachedDraftRequested>(this);
        messenger.Register<SelectMailItemContainerEvent>(this);
        messenger.Register<WinoIntelligenceEntitlementChanged>(this);
        _ = RefreshIntelligenceEntitlementAsync();
    }

    public MailListPageViewModel ViewModel { get; }

    /// <summary>The shared projection the list adapter shows.</summary>
    public MailListProjection Projection { get; }

    /// <summary>The list's view while it exists (the fragment attaches and detaches itself).</summary>
    public MailListFragment? View { get; set; }

    /// <summary>Multi-select (the contextual action mode): taps select instead of opening.</summary>
    public bool IsSelectionMode => ViewModel.IsMultiSelectionModeEnabled;

    /// <summary>
    /// The controller of <paramref name="entry"/>, created on first use: by the route before the ViewModel is navigated to,
    /// or by the first view, which the navigation service shows before it activates the page. Main thread.
    /// </summary>
    public static MailListController? Attach(PageEntry entry)
    {
        if (From(entry) is { } existing) return existing;
        if (entry.IsReleased || entry.ViewModel is not MailListPageViewModel viewModel || MainApplication.Services is not { } services) return null;
        var controller = new MailListController(viewModel, entry.Dispatcher, services);
        entry.State[StateKey] = controller;
        return controller;
    }

    public static MailListController? From(PageEntry? entry)
        => entry is not null && entry.State.TryGetValue(StateKey, out var value) ? value as MailListController : null;

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MailListPageViewModel.MailListOptions))
            _ = _dispatcher.ExecuteOnUIThread(() => { if (!_disposed) Projection.SetOptions(ViewModel.MailListOptions ?? new MailListProjectionOptions()); });
    }

    // ---- Selection ----

    /// <summary>Publishes the rows the list shows as selected to the ViewModel (Windows WinoMailListView selection).</summary>
    public void PublishSelection(IReadOnlyList<MailListRow> selectedRows, MailListRow? activeRow = null)
    {
        if (_disposed) return;
        var result = MailListSelectionSnapshotBuilder.Build(selectedRows, Projection, activeRow);
        if (result.Signature == _lastPublishedSelection) return;
        _lastPublishedSelection = result.Signature;
        ViewModel.ApplyMailSelectionSnapshot(result.Snapshot);
    }

    public void ClearSelection()
    {
        if (View is { } view) view.ClearSelection();
        else PublishSelection(Array.Empty<MailListRow>());
    }

    // ---- Reader ----

    public void Receive(ActiveMailItemChangedEvent message)
    {
        var item = message.SelectedMailItemViewModel;
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_disposed) return;
            if (item is null)
            {
                _ = _navigation.CloseRenderingFrameAsync();
                return;
            }
            // Multi-select selects without opening.
            if (IsSelectionMode) return;
            ViewModel.NavigationService.Navigate(item.IsDraft ? WinoPage.ComposePage : WinoPage.MailRenderingPage, item, NavigationReferenceFrame.RenderingFrame);
        });
    }

    public void Receive(ClearMailSelectionsRequested message) => _ = _dispatcher.ExecuteOnUIThread(() => { if (!_disposed) ClearSelection(); });

    public void Receive(DisposeRenderingFrameRequested message) => _ = _dispatcher.ExecuteOnUIThread(() =>
    {
        if (_disposed) return;
        ClearSelection();
        _ = _navigation.CloseRenderingFrameAsync();
    });

    public void Receive(ComposeDetachedDraftRequested message)
    {
        if (message.Draft is not { } draft) return;
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_disposed) return;
            // The draft is not listed: drop the selection and open the composer on its own.
            ClearSelection();
            ViewModel.NavigationService.Navigate(WinoPage.ComposePage, draft, NavigationReferenceFrame.RenderingFrame);
        });
    }

    public void Receive(SelectMailItemContainerEvent message) => _ = SelectWhenReadyAsync(message);

    /// <summary>Selects a mail once the list shows it (a new draft, a notification), retrying while the folder loads.</summary>
    private async Task SelectWhenReadyAsync(SelectMailItemContainerEvent message)
    {
        for (int attempt = 0; attempt < 15 && !_disposed; attempt++)
        {
            bool selected = false;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (_disposed) return;
                foreach (var group in Projection.Groups)
                {
                    foreach (var row in group)
                    {
                        if (row.SourceItem.StableId != message.MailUniqueId && !(row.IsThreadHead && row.LeafItems.Any(leaf => leaf.StableId == message.MailUniqueId))) continue;
                        if (View is { } view) view.SelectRow(row, message.ScrollToItem);
                        else PublishSelection([row]);
                        selected = true;
                        return;
                    }
                }
            });
            if (selected) return;
            await Task.Delay(200);
        }
    }

    // ---- Wino Intelligence entitlement (Windows MailListPage.ApplyIntelligenceEntitlementAsync) ----

    public void Receive(WinoIntelligenceEntitlementChanged message)
    {
        bool canAccess = message.Entitlement.CanAccessSurfaces;
        _ = _dispatcher.ExecuteOnUIThread(() => { _ = ApplyIntelligenceEntitlementAsync(canAccess); });
    }

    private async Task RefreshIntelligenceEntitlementAsync()
    {
        if (_entitlements is null) return;
        try
        {
            var entitlement = await _entitlements.GetEntitlementAsync().ConfigureAwait(false);
            bool canAccess = entitlement.CanAccessSurfaces;
            await _dispatcher.ExecuteOnUIThread(() => { _ = ApplyIntelligenceEntitlementAsync(canAccess); });
        }
        catch (Exception exception) { Serilog.Log.Warning(exception, "The intelligence entitlement could not be read."); }
    }

    private async Task ApplyIntelligenceEntitlementAsync(bool canAccess)
    {
        if (_disposed) return;
        await ViewModel.ApplyIntelligenceEntitlementAsync(canAccess);
        View?.RefreshRows();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        ViewModel.PropertyChanged -= ViewModelChanged;
        Projection.Dispose();
        View = null;
    }
}
