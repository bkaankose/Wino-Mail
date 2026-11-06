using System.Collections.Specialized;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using AndroidX.SwipeRefreshLayout.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Chip;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.ProgressIndicator;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Shell;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Contacts;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Contacts;

/// <summary>
/// The Contacts mode's content page (Windows ContactsPage list; canvas Material pass Contacts; the iPhone and Mac
/// contacts pages) over the shared <see cref="ContactsPageViewModel"/>: the 64 dp top app bar with the selected filter as
/// its title and search, the filter chips (the primary filters, the active account's address books and lists, and the
/// account as a dropdown chip when there are several; categories stay in the drawer), the letter-indexed list with the
/// Wino fast scroller, paging, pull to refresh (the mode's synchronization), the long-press menu (the Windows contact
/// card menu), the empty and readiness states and the New contact FAB. A tap opens the contact card
/// (<see cref="ContactCardFragment"/>): full screen on compact widths, in the reader pane beside the list otherwise.
/// </summary>
public sealed class ContactsFragment : WinoFragment<ContactsPageViewModel>
{
    internal const string CardKey = "contacts.card";

    private readonly Dictionary<Guid, Bitmap?> _pictures = new();
    private readonly List<ContactGroup> _observedGroups = [];
    private IPictureStorageService? _pictureStorage;
    private AndroidNavigationService? _navigation;
    private MaterialToolbar? _toolbar;
    private ChipGroup? _chips;
    private View? _chipScroll;
    private RecyclerView? _list;
    private LinearLayoutManager? _layout;
    private ContactsListAdapter? _adapter;
    private SwipeRefreshLayout? _refresh;
    private WinoIndexScroller? _index;
    private PeopleStatePanel? _empty;
    private CircularProgressIndicator? _loading;
    private IReadOnlyList<AccountContactViewModel>? _searchResults;
    private CancellationTokenSource? _searchCancellation;
    private bool _reloadScheduled;

    protected override int LayoutResource => Resource.Layout.fragment_contacts;

    private IShellChrome? Chrome => ParentFragment as IShellChrome;

    protected override void OnBind(View view, ContactsPageViewModel viewModel, IDispatcher dispatcher)
    {
        var services = MainApplication.Services ?? throw new InvalidOperationException("The application services are not ready.");
        var context = view.RequireContext();
        _pictureStorage = services.GetService<IPictureStorageService>();
        _navigation = services.GetService<AndroidNavigationService>();

        BuildToolbar(view, context, viewModel, dispatcher);
        _chipScroll = view.FindViewById(Resource.Id.contacts_chip_scroll);
        _chips = view.FindViewById<ChipGroup>(Resource.Id.contacts_chips);
        BuildList(view, context, viewModel, dispatcher);

        if (view.FindViewById<FloatingActionButton>(Resource.Id.contacts_fab) is { } fab)
        {
            fab.SetImageDrawable(WinoIcons.Drawable(context, WinoIconGlyph.Add, WinoTokens.Dp(context, Resource.Dimension.wino_m3_fab_glyph), WinoColors.OnPrimaryContainer(context)));
            WinoMaterialAccent.Tint(fab);
            fab.ContentDescription = Translator.ContactsPane_NewContact;
            BindCommand(fab, viewModel.AddContactCommand, dispatcher);
        }

        if (view.FindViewById(Resource.Id.contacts_empty) is { } emptyView)
        {
            _empty = new PeopleStatePanel(emptyView, WinoIconGlyph.People);
        }
        _loading = view.FindViewById<CircularProgressIndicator>(Resource.Id.contacts_loading);
        if (view.FindViewById(Resource.Id.contacts_blocked_host) is { } blockedHost && view.FindViewById(Resource.Id.contacts_blocked) is { } blocked)
            PeopleViews.BindReadiness(Bindings, blockedHost, blocked, viewModel.Readiness, dispatcher, WinoIconGlyph.People);

        // The filters follow the shared pane menu (the drawer shows the same items).
        NotifyCollectionChangedEventHandler menuChanged = (_, _) => _ = dispatcher.ExecuteOnUIThread(ScheduleChips);
        if (viewModel.ShellMenu?.Items is INotifyCollectionChanged items)
        {
            items.CollectionChanged += menuChanged;
            Bindings.Own(new ActionDisposable(() => items.CollectionChanged -= menuChanged));
        }
        NotifyCollectionChangedEventHandler filtersChanged = (_, _) => _ = dispatcher.ExecuteOnUIThread(ScheduleChips);
        viewModel.FilterGroups.CollectionChanged += filtersChanged;
        Bindings.Own(new ActionDisposable(() => viewModel.FilterGroups.CollectionChanged -= filtersChanged));

        Bind(viewModel, dispatcher, nameof(ContactsPageViewModel.SelectedFilter), static vm => vm.SelectedFilter, filter =>
        {
            if (_searchBar is not null) _searchBar.Hint = $"{Translator.SearchBar_SearchIn} {filter?.Name ?? Translator.ContactsPage_Title}";
            ScheduleChips();
        });
        Bind(viewModel, dispatcher, nameof(ContactsPageViewModel.SelectedContact), static vm => vm.SelectedContact, contact =>
            _adapter?.SetSelected(Chrome?.WidthClass is WindowWidthClass.Compact or null ? null : contact?.Id));
        Bind(viewModel, dispatcher, nameof(ContactsPageViewModel.IsLoading), static vm => vm.IsLoading, _ => UpdateState());
        Bind(viewModel, dispatcher, nameof(ContactsPageViewModel.IsEmpty), static vm => vm.IsEmpty, _ => UpdateState());
        Bind(viewModel, dispatcher, nameof(ContactsPageViewModel.IsRefreshing), static vm => vm.IsRefreshing, refreshing =>
        {
            if (!refreshing && _refresh is not null) _refresh.Refreshing = false;
        });

        Bindings.Own(new ActionDisposable(() =>
        {
            ReleaseGroups();
            _searchCancellation?.Cancel();
            _pictures.Clear();
        }));
        ScheduleChips();
        ScheduleReload();
    }

    // ---- Docked search bar (the same in every mode): menu, "Search in <filter>", overflow ----

    private const int MenuNewContact = 2;
    private const int MenuNewList = 3;

    private Shell.DockedSearchBar? _searchBar;

    private void BuildToolbar(View view, Context context, ContactsPageViewModel viewModel, IDispatcher dispatcher)
    {
        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.contacts_toolbar);
        if (_toolbar is null) return;
        _searchBar = Shell.DockedSearchBar.Attach(_toolbar, this, $"{Translator.SearchBar_SearchIn} {Translator.ContactsPage_Title}", () => Chrome?.ToggleNavigation());
        _searchBar.QueryChanged += text => Search(text, select: false);
        _searchBar.Submitted += text => Search(text, select: true);
        var menu = _toolbar.Menu;
        menu.Clear();
        menu.Add(0, MenuNewContact, 1, Translator.ContactsPane_NewContact);
        menu.Add(0, MenuNewList, 2, Translator.ContactList_NewTitle);
        _toolbar.MenuItemClick += (_, args) =>
        {
            switch (args.Item?.ItemId)
            {
                case MenuNewContact:
                    if (viewModel.AddContactCommand.CanExecute(null)) viewModel.AddContactCommand.Execute(null);
                    break;
                case MenuNewList:
                    if (viewModel.CreateListCommand.CanExecute(null)) viewModel.CreateListCommand.Execute(null);
                    break;
            }
        };
        Bind(viewModel, dispatcher, nameof(ContactsPageViewModel.HasCreateDestinations), static vm => vm.HasCreateDestinations, value => menu.FindItem(MenuNewContact)?.SetEnabled(value));
        Bind(viewModel, dispatcher, nameof(ContactsPageViewModel.HasListDestinations), static vm => vm.HasListDestinations, value => menu.FindItem(MenuNewList)?.SetVisible(value));
    }

    // ---- Filter chips (the shared pane filters) ----

    private bool _chipsScheduled;

    private void ScheduleChips()
    {
        if (_chipsScheduled || View is not { } view) return;
        _chipsScheduled = true;
        view.Post(() =>
        {
            _chipsScheduled = false;
            if (View is not null) BuildChips();
        });
    }

    private void BuildChips()
    {
        if (_chips is null || ViewModel is not { } viewModel || View?.Context is not { } context) return;
        _chips.RemoveAllViews();
        // Expanded widths pin the pane with the same filters (Windows layout): no chip row.
        if (Chrome?.WidthClass == WindowWidthClass.Expanded)
        {
            if (_chipScroll is not null) _chipScroll.Visibility = ViewStates.Gone;
            return;
        }
        var items = viewModel.ShellMenu?.Items.ToArray() ?? [];
        var accounts = items.OfType<ContactFilterViewModel>().Where(filter => filter.Kind == ContactFilterKind.Account).ToList();
        foreach (var filter in items.OfType<ContactFilterViewModel>())
        {
            if (filter.Kind is ContactFilterKind.Account or ContactFilterKind.Category) continue;
            var chip = WinoChips.Filter(context, filter.Name ?? string.Empty, ReferenceEquals(filter, viewModel.SelectedFilter));
            chip.Enabled = filter.IsEnabled;
            var target = filter;
            chip.Click += (_, _) =>
            {
                // A filter chip selects; tapping the selected one keeps it (the pane has no "none" state).
                chip.Checked = true;
                PeopleViews.Observe(viewModel.OnMenuItemInvokedAsync(target), ReportError);
            };
            _chips.AddView(chip);
        }
        if (accounts.Count > 1)
        {
            // Several accounts: the active one as a dropdown chip (the pane's account rows).
            var active = accounts.FirstOrDefault(filter => filter.IsSelected) ?? accounts[0];
            var chip = WinoChips.Assist(context, active.Name ?? string.Empty);
            chip.ContentDescription = Translator.AccountPickerDialog_Title;
            void Pick() => WinoChoiceSheet.Show(context, Translator.AccountPickerDialog_Title, accounts.Select(account => account.Name ?? string.Empty).ToList(),
                accounts.IndexOf(active), index => PeopleViews.Observe(viewModel.OnMenuItemInvokedAsync(accounts[index]), ReportError));
            chip.Click += (_, _) => Pick();
            chip.SetOnCloseIconClickListener(new ClickListener(Pick));
            _chips.AddView(chip);
        }
        if (_chipScroll is not null) _chipScroll.Visibility = _chips.ChildCount == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    private sealed class ClickListener(Action clicked) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? view) => clicked();
    }

    // ---- List ----

    private void BuildList(View view, Context context, ContactsPageViewModel viewModel, IDispatcher dispatcher)
    {
        _list = view.FindViewById<RecyclerView>(Resource.Id.contacts_list);
        _refresh = view.FindViewById<SwipeRefreshLayout>(Resource.Id.contacts_refresh);
        if (_list is null) return;
        _layout = new LinearLayoutManager(context);
        _list.SetLayoutManager(_layout);
        _list.ContentDescription = Translator.Accessibility_ContactList;
        _adapter = new ContactsListAdapter(Map);
        _adapter.ContactClicked += (_, contact) => Open(contact);
        _adapter.ContactLongClicked += (_, args) => ShowMenu(args.Contact, args.Anchor);
        _list.SetAdapter(_adapter);
        _list.AddOnScrollListener(new ScrollListener(this));
        if (_refresh is not null)
        {
            _refresh.SetColorSchemeColors(WinoColors.Accent(context).ToArgb());
            _refresh.Refresh += (_, _) =>
            {
                if (viewModel.RefreshContactsCommand.CanExecute(null)) PeopleViews.Observe(RefreshAsync(viewModel), ReportError);
                else _refresh.Refreshing = false;
            };
        }

        // The Wino fast scroller on the end edge of the zone.
        if (view.FindViewById<FrameLayout>(Resource.Id.contacts_frame) is { } frame)
        {
            _index = new WinoIndexScroller(context) { ContentDescription = Translator.Accessibility_ContactList };
            _index.SectionSelected += (_, section) =>
            {
                if (_adapter is null || section < 0 || section >= _adapter.Sections.Count) return;
                _layout.ScrollToPositionWithOffset(_adapter.Sections[section].Position, 0);
            };
            frame.AddView(_index, 1, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent, GravityFlags.End));
        }

        viewModel.ContactGroups.CollectionChanged += GroupsChanged;
        Bindings.Own(new ActionDisposable(() => viewModel.ContactGroups.CollectionChanged -= GroupsChanged));
        ObserveGroups();
    }

    private async Task RefreshAsync(ContactsPageViewModel viewModel)
    {
        try { await viewModel.RefreshContactsCommand.ExecuteAsync(null); }
        finally
        {
            if (Entry is { } entry) await entry.Dispatcher.ExecuteOnUIThread(() => { if (_refresh is not null) _refresh.Refreshing = false; });
        }
    }

    private WinoContactRowModel Map(AccountContactViewModel contact)
        => new(contact.Name ?? contact.Address ?? string.Empty, contact.SecondaryValue, contact.Address ?? contact.Name,
            PeopleViews.LoadPicture(_pictureStorage, _pictures, contact.ContactPictureFileId, ReportError), contact.IsFavorite);

    private void GroupsChanged(object? sender, NotifyCollectionChangedEventArgs args) => OnUI(() => { ObserveGroups(); ScheduleReload(); });

    private void GroupChanged(object? sender, NotifyCollectionChangedEventArgs args) => OnUI(ScheduleReload);

    private void ObserveGroups()
    {
        ReleaseGroups();
        if (ViewModel is not { } viewModel) return;
        foreach (var group in viewModel.ContactGroups)
        {
            group.CollectionChanged += GroupChanged;
            _observedGroups.Add(group);
        }
    }

    private void ReleaseGroups()
    {
        foreach (var group in _observedGroups) group.CollectionChanged -= GroupChanged;
        _observedGroups.Clear();
    }

    private void OnUI(Action action)
    {
        if (Entry is { } entry) _ = entry.Dispatcher.ExecuteOnUIThread(() => { if (View is not null) action(); });
    }

    /// <summary>Coalesces burst changes into one rebuild on the next main-loop turn.</summary>
    private void ScheduleReload()
    {
        if (_reloadScheduled || View is not { } view) return;
        _reloadScheduled = true;
        view.Post(() =>
        {
            _reloadScheduled = false;
            if (View is not null) Rebuild();
        });
    }

    private void Rebuild()
    {
        if (_adapter is null || ViewModel is not { } viewModel) return;
        if (_searchResults is { } results)
        {
            _adapter.SetSections(results.GroupBy(contact => contact.InitialLetter ?? string.Empty)
                .Select(group => (group.Key, (IReadOnlyList<AccountContactViewModel>)group.ToList())));
        }
        else
        {
            _adapter.SetSections(viewModel.ContactGroups.ToArray().Select(group => (group.Key, (IReadOnlyList<AccountContactViewModel>)group.ToList())));
        }
        if (_index is not null)
        {
            _index.Sections = _searchResults is null ? _adapter.Sections.Select(section => section.Letter).ToList() : [];
            UpdateActiveSection();
        }
        _adapter.SetSelected(Chrome?.WidthClass is WindowWidthClass.Compact or null ? null : viewModel.SelectedContact?.Id);
        UpdateState();
    }

    private void UpdateState()
    {
        if (ViewModel is not { } viewModel) return;
        bool loading = viewModel.IsLoading && _refresh?.Refreshing != true;
        if (_loading is not null) _loading.Visibility = loading ? ViewStates.Visible : ViewStates.Gone;
        bool empty = _searchResults is null ? viewModel.IsEmpty : _searchResults.Count == 0;
        if (_empty is not null)
        {
            _empty.Visible = empty && !viewModel.IsLoading;
            _empty.Apply(Translator.ContactsPage_NoContacts, null,
                _searchResults is null && viewModel.HasCreateDestinations ? Translator.ContactsPage_AddFirstContact : null,
                () => { if (viewModel.AddContactCommand.CanExecute(null)) viewModel.AddContactCommand.Execute(null); });
        }
    }

    private void UpdateActiveSection()
    {
        if (_index is null || _adapter is null || _layout is null) return;
        int first = _layout.FindFirstVisibleItemPosition();
        _index.ActiveSection = first < 0 ? -1 : _adapter.SectionOf(first);
    }

    private void Scrolled()
    {
        UpdateActiveSection();
        if (_layout is null || _adapter is null || ViewModel is not { } viewModel || _searchResults is not null) return;
        if (_layout.FindLastVisibleItemPosition() < _adapter.ItemCount - 12 || !viewModel.LoadMoreContactsCommand.CanExecute(null)) return;
        PeopleViews.Observe(viewModel.LoadMoreContactsCommand.ExecuteAsync(null), ReportError);
    }

    private sealed class ScrollListener(ContactsFragment owner) : RecyclerView.OnScrollListener
    {
        public override void OnScrolled(RecyclerView recyclerView, int dx, int dy) => owner.Scrolled();
    }

    // ---- Open, menu ----

    private void Open(AccountContactViewModel contact)
    {
        if (ViewModel is not { } viewModel || _navigation is not { } navigation) return;
        viewModel.SelectedContacts.Clear();
        viewModel.SelectedContacts.Add(contact);
        viewModel.SelectedContact = contact;
        PeopleViews.Observe(navigation.ShowDetailAsync(CardKey, static () => new ContactCardFragment()), ReportError);
    }

    /// <summary>The Windows contact card menu: Send mail, Edit, Favourite, Add to list, Remove from this list, Delete.</summary>
    private void ShowMenu(AccountContactViewModel contact, View anchor)
    {
        if (ViewModel is not { } viewModel) return;
        var context = anchor.RequireContext();
        var targets = viewModel.ResolveContactContextTargets(contact);
        var popup = new PopupMenu(context, anchor, GravityFlags.End);
        var actions = new Dictionary<int, Action>();
        void Add(string title, bool enabled, Action run)
        {
            int id = actions.Count + 1;
            actions[id] = run;
            popup.Menu.Add(0, id, id, title)?.SetEnabled(enabled);
        }
        Add(Translator.ContactAction_SendMail, targets.Any(item => item.CanSendMail), () => viewModel.ComposeToContacts(targets));
        if (targets.Count == 1) Add(Translator.ContactAction_Edit, contact.CanEdit, () => viewModel.EditContactCommand.Execute(contact));
        var anyNotFavorite = targets.Any(item => !item.IsFavorite);
        Add(anyNotFavorite ? Translator.ContactAction_Favorite : Translator.ContactAction_Unfavorite, true,
            () => PeopleViews.Observe(viewModel.FavoriteContactsAsync(targets), ReportError));
        if (viewModel.ContactLists.Any(list => targets.All(item => item.SourceContact.AddressBookId == list.AddressBookId)))
            Add(Translator.ContactAction_AddToList, true, () => PeopleViews.Observe(AssignToListAsync(context, viewModel, targets), ReportError));
        if (viewModel.SelectedFilter?.IsList == true)
            Add(Translator.ContactAction_RemoveFromList, true, () => PeopleViews.Observe(RemoveFromListAsync(viewModel, targets), ReportError));
        Add(Translator.ContactAction_Delete, targets.Any(item => item.CanDelete), () => PeopleViews.Observe(viewModel.DeleteContactsAsync(targets), ReportError));
        popup.MenuItemClick += (_, args) => { if (args.Item is { } item && actions.TryGetValue(item.ItemId, out var run)) run(); };
        popup.Show();
    }

    private async Task AssignToListAsync(Context context, ContactsPageViewModel viewModel, IReadOnlyList<AccountContactViewModel> targets)
    {
        var lists = await viewModel.GetAssignableListsAsync(targets);
        var ids = targets.Select(contact => contact.Id).ToList();
        await (Entry?.Dispatcher.ExecuteOnUIThread(() =>
        {
            if (lists.Count == 0)
            {
                WinoChoiceSheet.Show(context, Translator.ContactAction_AddToList, [Translator.ContactsShellMac_NoAssignableLists], -1, _ => { });
                return;
            }
            WinoChoiceSheet.Show(context, Translator.ContactAction_AddToList, lists.Select(list => list.Name ?? string.Empty).ToList(), -1,
                index => PeopleViews.Observe(viewModel.AssignContactsToListAsync(lists[index], ids), ReportError));
        }) ?? Task.CompletedTask);
    }

    private static async Task RemoveFromListAsync(ContactsPageViewModel viewModel, IReadOnlyList<AccountContactViewModel> targets)
    {
        foreach (var contact in targets.ToList()) await viewModel.RemoveFromCurrentListCommand.ExecuteAsync(contact);
    }

    // ---- Search: the shared contact search, grouped by initial ----

    private void Search(string text, bool select) => PeopleViews.Observe(SearchAsync(text, select), ReportError);

    private async Task SearchAsync(string text, bool select)
    {
        _searchCancellation?.Cancel();
        _searchCancellation = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (_searchResults is null) return;
            _searchResults = null;
            ScheduleReload();
            return;
        }
        if (ViewModel is not { } viewModel || Entry is not { } entry) return;
        var cancellation = _searchCancellation = new CancellationTokenSource();
        try
        {
            if (!select) await Task.Delay(ContactsPageViewModel.SearchDebounce, cancellation.Token);
            var results = await viewModel.SearchContactsAsync(text, ContactsPageViewModel.SearchLimit);
            if (cancellation.IsCancellationRequested) return;
            await entry.Dispatcher.ExecuteOnUIThread(() =>
            {
                if (View is null) return;
                _searchResults = results;
                Rebuild();
            });
            if (select && results.Count > 0 && await viewModel.LoadAndSelectContactAsync(results[0].Id) is { } loaded)
                await entry.Dispatcher.ExecuteOnUIThread(() => Open(loaded));
        }
        catch (OperationCanceledException) { }
    }
}
