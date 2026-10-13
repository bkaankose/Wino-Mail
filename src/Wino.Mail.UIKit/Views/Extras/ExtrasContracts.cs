namespace Wino.Mail.UIKit.Views.Extras;

/// <summary>
/// Supplies the Daily briefing panel to the shells (Mac IDailyBriefingPresenter) and tracks the unseen badge. One panel
/// controller serves both idioms: the iPad shell hosts it as an overlay panel beside the reader
/// (<see cref="CreatePanel"/>, <see cref="DailyBriefingPanelViewController.PanelWidth"/> wide), the iPhone shell presents
/// it as a sheet (<see cref="PresentSheet"/>). Main-thread members.
/// </summary>
public interface IUIKitDailyBriefingPresenter
{
    /// <summary>
    /// Creates the panel controller once and returns it again later; the shell keeps and re-shows it. <paramref name="close"/>
    /// runs when the panel's close button or the ViewModel asks to close (the most recent caller's action wins).
    /// </summary>
    DailyBriefingPanelViewController CreatePanel(Action close);

    /// <summary>Presents the panel as a sheet (medium and large detents) from the top-most controller; iPhone.</summary>
    void PresentSheet();

    /// <summary>True when there are unseen briefing items (the toolbar or tab badge).</summary>
    bool HasUnseenItems { get; }

    /// <summary>True when the briefing can be shown at all (Windows hides the title-bar button otherwise).</summary>
    bool IsAvailable { get; }

    /// <summary>Raised on the main thread when <see cref="HasUnseenItems"/> or <see cref="IsAvailable"/> changed.</summary>
    event EventHandler? UnseenItemsChanged;
}

/// <summary>
/// Opens What's New (Mac IWhatsNewPresenter): a page sheet on iPhone and a form sheet on iPad. It also answers
/// <c>WhatsNewOpenRequested</c> (About page) and marks the running version as seen.
/// </summary>
public interface IUIKitWhatsNewPresenter
{
    Task ShowAsync();
}
