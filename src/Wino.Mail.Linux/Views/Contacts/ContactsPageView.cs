using System.Runtime.CompilerServices;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Common;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>
/// Contacts mode content (Windows ContactsPage, macOS ContactsPageViewController): the alphabetised contact list and
/// the 348 px detail pane at the trailing edge, laid out by <see cref="EndPaneLayout"/> on the page's own width, and the
/// blocked state over both. The ViewModel outlives the page (it publishes the sidebar menu), so every subscription on it
/// is owned by <c>Bindings</c>. Later packages extend the page through the partial hooks in their own files.
/// </summary>
internal sealed partial class ContactsPageView : WinoView<ContactsPageViewModel>, IShellSearchTarget
{
    private const int DetailWidth = 348;
    private const int WideMinimum = 728;
    private const int SwapMaximum = 559;

    private readonly StrongBox<ContactsPageView?> _self;
    private readonly Gtk.Overlay _root = Gtk.Overlay.New();
    private readonly EndPaneLayout _layout = new(DetailWidth, WideMinimum, SwapMaximum) { KeepsEndWhenWide = true };
    private readonly Adw.Bin _detailHost = Adw.Bin.New();
    private readonly ModeReadinessPanel _readiness;
    private readonly ContactPictures _pictures;
    private readonly INavigationService _navigation;
    private readonly IActivationStateService _activationState;
    private CancellationTokenSource? _search;

    public ContactsPageView(ContactsPageViewModel viewModel, WinoViewServices services, IPictureStorageService pictureStorage,
        INavigationService navigation, IActivationStateService activationState)
        : base(viewModel, services)
    {
        _navigation = navigation;
        _activationState = activationState;
        _self = new StrongBox<ContactsPageView?>(this);
        Bindings.Own(new ActionDisposable(() => _self.Value = null));
        _pictures = Bindings.Own(new ContactPictures(pictureStorage, Dispatcher, ReportError));
        _readiness = Bindings.Own(new ModeReadinessPanel(ViewModel.Readiness, WinoIconGlyph.People, Dispatcher, ReportError));
        Bindings.Own(_layout);

        _detailHost.SetChild(BuildNoSelection());
        _layout.Start = BuildList();
        _layout.End = _detailHost;
        _root.SetChild(_layout.Root);
        _root.AddOverlay(_readiness.Root);

        var self = _self;
        Action stateChanged = () => self.Value?.LayoutStateChanged(self.Value._layout.State);
        _layout.StateChanged += stateChanged;
        Bindings.Own(new ActionDisposable(() => _layout.StateChanged -= stateChanged));

        BuildDetail(_detailHost);
    }

    public override Gtk.Widget Root => _root;

    internal EndPaneLayout Layout => _layout;

    internal ContactPictures Pictures => _pictures;

    internal bool IsBlocked => _readiness.IsShown;

    string? IShellSearchTarget.SearchPlaceholder => Translator.ContactsPage_SearchPlaceholder;

    partial void BuildDetail(Adw.Bin host);
    partial void BindDetail();
    partial void LayoutStateChanged(EndPaneState state);
    partial void ContextCellSetup(ContactCell cell);
    partial void ContextCellTeardown(ContactCell cell);
    partial void DragCellSetup(ContactCell cell);
    partial void DragCellTeardown(ContactCell cell);

    // Awaited, unlike the async-void OnNavigatedTo: back from the editor the coordinator calls OnNavigatedBack after the
    // page activated, and the saved contact can only be selected once the list reconciled.
    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
#if DEBUG
        DebugCurrent = new WeakReference<ContactsPageView>(this);
#endif
        Bind.Bind(nameof(ContactsPageViewModel.SelectedContact), static vm => vm.SelectedContact is not null, shows => _layout.ShowsEnd = shows);
        BindList();
        BindDetail();
        // The Linux shell activates every content page as a new navigation, also on Back, and the shared ViewModel opens
        // the editor for an import parameter on every new navigation: the same import is handed over once only.
        if (parameter is ContactEditNavigationParameter { ImportDraft: not null } import && !HandledImports.TryAdd(import, Handled)) parameter = null;
        return ViewModel.InitializeNavigationAsync(mode, parameter, awaitReadiness: false);
    }

    private static readonly ConditionalWeakTable<ContactEditNavigationParameter, object> HandledImports = [];
    private static readonly object Handled = new();

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (DebugCurrent is not null && DebugCurrent.TryGetTarget(out var current) && ReferenceEquals(current, this)) DebugCurrent = null;
#endif
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
        return base.DeactivateAsync();
    }

#if DEBUG
    internal static WeakReference<ContactsPageView>? DebugCurrent { get; private set; }
#endif

    public async Task SearchTextChangedAsync(string text)
    {
        var token = RestartSearch();
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowSearchResults(null);
            return;
        }
        try
        {
            await Task.Delay(ContactsPageViewModel.SearchDebounce, token);
            await RunSearchAsync(text, token, selectFirst: false);
        }
        catch (OperationCanceledException) { }
    }

    public async Task SearchSubmittedAsync(string text)
    {
        var token = RestartSearch();
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowSearchResults(null);
            return;
        }
        try { await RunSearchAsync(text, token, selectFirst: true); }
        catch (OperationCanceledException) { }
    }

    public Task SearchClearedAsync()
    {
        RestartSearch();
        ShowSearchResults(null);
        return Task.CompletedTask;
    }

    private CancellationToken RestartSearch()
    {
        _search?.Cancel();
        _search?.Dispose();
        _search = new CancellationTokenSource();
        return _search.Token;
    }

    private async Task RunSearchAsync(string text, CancellationToken token, bool selectFirst)
    {
        var results = await ViewModel.SearchContactsAsync(text, ContactsPageViewModel.SearchLimit);
        if (token.IsCancellationRequested || IsReleased) return;
        AccountContactViewModel[] rows = [.. results];
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (token.IsCancellationRequested || IsReleased) return;
            ShowSearchResults(rows);
        });
        if (!selectFirst || rows.Length == 0 || token.IsCancellationRequested || IsReleased) return;
        AccountContactViewModel? selected = await ViewModel.LoadAndSelectContactAsync(rows[0].Id);
        if (selected is null || IsReleased) return;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (!IsReleased && ViewModel.SelectedContact is null) ViewModel.SelectedContact = rows[0];
            if (!IsReleased) ApplySelectedContact(scroll: true);
        });
    }

    /// <summary>
    /// The shared command stores a mailto address for the mail shell, which only listens while Mail is the active mode
    /// (it reads a pending address when it activates); the head switches to Mail so the composer opens at once.
    /// </summary>
    internal void ComposeTo(AccountContactViewModel[] contacts)
    {
        ViewModel.ComposeToContacts(contacts);
        if (_activationState.MailToUri is not null) _navigation.ChangeApplicationMode(WinoApplicationMode.Mail);
    }

    private Gtk.Widget BuildNoSelection()
    {
        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        column.SetHalign(Gtk.Align.Center);
        column.SetValign(Gtk.Align.Center);
        column.AddCssClass(WinoCssClass.ContactEmpty);
        var icon = new WinoIcon(WinoIconGlyph.Person, 64).Root;
        icon.AddCssClass("dim-label");
        column.Append(icon);
        var text = Gtk.Label.New(Translator.ContactsPage_NoContactSelected);
        text.AddCssClass("dim-label");
        text.SetWrap(true);
        text.SetJustify(Gtk.Justification.Center);
        text.SetMaxWidthChars(28);
        column.Append(text);
        return column;
    }
}
