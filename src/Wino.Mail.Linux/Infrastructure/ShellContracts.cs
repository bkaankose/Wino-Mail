namespace Wino.Mail.Linux.Infrastructure;

/// <summary>Implemented by the shell's content view to receive the header search entry.</summary>
internal interface IShellSearchTarget
{
    Task SearchTextChangedAsync(string text);
    Task SearchSubmittedAsync(string text);
    Task SearchClearedAsync();

    /// <summary>The entry's placeholder while this page is current; null keeps the generic one.</summary>
    string? SearchPlaceholder => null;
}

/// <summary>Supplies the Daily briefing panel. Optional: the header button stays hidden while none is registered.</summary>
internal interface IDailyBriefingPresenter
{
    Gtk.Widget CreatePanel(Action close);
    bool HasUnseenItems { get; }
    bool IsAvailable { get; }
    event EventHandler? UnseenItemsChanged;
}

/// <summary>Opens the What's New window. Optional.</summary>
internal interface IWhatsNewPresenter
{
    Task ShowAsync();
}

/// <summary>Opens the Wino Account popover or sign-in from the header button. Optional.</summary>
internal interface IWinoAccountPresenter
{
    void Show(Gtk.Widget anchor);
}

/// <summary>A search target that shows suggestions under the header entry. Called on the main thread.</summary>
internal interface IShellSearchSuggestionTarget
{
    /// <summary>The header entry while this page is the search target; null when the page loses it.</summary>
    void SetSearchAnchor(Gtk.Widget? entry);

    /// <summary>Up (-1) or Down (+1) in the entry; true when the list took the key.</summary>
    bool MoveSearchSuggestion(int delta);

    /// <summary>Enter in the entry; true when a highlighted row was chosen, and the header then submits nothing.</summary>
    bool TryChooseSearchSuggestion();

    /// <summary>Escape in the entry; true when an open list was closed, and the header then keeps the text and the focus.</summary>
    bool CloseSearchSuggestions();

    void SearchFocusLeft();
}

/// <summary>A page or reading-pane view the keyboard can move into (F6, Enter, Escape). Called on the main thread.</summary>
internal interface IPaneFocus
{
    /// <summary>Moves the focus to the view's main element (the selected row, the reader's body, the first empty composer field); false when it cannot take it.</summary>
    bool FocusPrimary();
}

/// <summary>A page with a detail pane of its own (the contact detail, the task drawer). Called on the main thread.</summary>
internal interface IDetailPane
{
    bool HasDetail { get; }

    /// <summary>The pane's widget, to tell whether the focus is inside it.</summary>
    Gtk.Widget? DetailRoot { get; }

    bool FocusDetail();

    /// <summary>Escape inside the detail: true when the page closed a detail that covered its list (the narrow layout).</summary>
    bool CloseDetail();

    /// <summary>Set by the shell: the page calls it when Enter on a list row asks for the detail.</summary>
    Action? DetailRequested { set; }
}

/// <summary>Alt+Down and Alt+Up: the next or previous message, contact, task or calendar period.</summary>
internal interface IItemNavigation
{
    bool MoveItem(int delta);
}

/// <summary>A page or reading-pane view that runs fixed keys of its own (print, zoom, junk, today); false when the key does not apply now.</summary>
internal interface IKeyActionTarget
{
    bool TryHandleKeyAction(string action);
}
