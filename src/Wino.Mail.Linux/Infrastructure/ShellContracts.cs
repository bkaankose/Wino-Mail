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
