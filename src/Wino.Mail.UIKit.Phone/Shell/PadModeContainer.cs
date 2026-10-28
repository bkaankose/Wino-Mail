using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Shell;

/// <summary>
/// A tab's navigation stack. On iPad in a wide landscape window (<see cref="PadModeContainer"/>) a push from the stack's
/// root shows the page beside the list instead, and pops close it there, so the iPhone pages keep their push and pop
/// calls. Everywhere else it is a plain navigation controller.
/// </summary>
internal sealed class ModeStackController : UINavigationController
{
    public ModeStackController(UIViewController root) : base(root) { }

    /// <summary>The iPad container while it shows the list beside the item, else null.</summary>
    internal PadModeContainer? Pairing { get; set; }

    internal bool IsPaired => Pairing is { IsPaired: true };

    public override void PushViewController(UIViewController viewController, bool animated)
    {
        if (Pairing is { IsPaired: true } pairing && (ViewControllers?.Length ?? 0) == 1)
        {
            pairing.ShowItem(viewController);
            return;
        }
        base.PushViewController(viewController, animated);
    }

    public override UIViewController? PopViewController(bool animated)
    {
        if (Pairing is { IsPaired: true, HasItem: true } pairing && (ViewControllers?.Length ?? 0) == 1) return pairing.CloseItem().LastOrDefault();
        return base.PopViewController(animated);
    }

    public override UIViewController[]? PopToViewController(UIViewController viewController, bool animated)
    {
        if (Pairing is { IsPaired: true, HasItem: true } pairing && ViewControllers is [var root] && ReferenceEquals(root, viewController)) return pairing.CloseItem();
        return base.PopToViewController(viewController, animated);
    }

    public override UIViewController[]? PopToRootViewController(bool animated)
    {
        if (Pairing is { IsPaired: true, HasItem: true } pairing && (ViewControllers?.Length ?? 0) == 1) return pairing.CloseItem();
        return base.PopToRootViewController(animated);
    }

    internal void PushDirect(UIViewController[] controllers) => base.SetViewControllers(controllers, false);
}

/// <summary>The stack as its pages see it: the item shown beside the list counts as pushed.</summary>
internal static class ModeStackExtensions
{
    public static UIViewController[] LogicalViewControllers(this UINavigationController stack)
        => stack is ModeStackController { Pairing: { IsPaired: true, HasItem: true } pairing }
            ? [.. stack.ViewControllers ?? [], .. pairing.ItemControllers]
            : stack.ViewControllers ?? [];

    public static UIViewController? LogicalTop(this UINavigationController stack)
        => stack is ModeStackController { Pairing: { IsPaired: true, HasItem: true } pairing } ? pairing.ItemControllers.LastOrDefault() : stack.TopViewController;
}

/// <summary>
/// iPad (owner, 27 Oct 2026): a tab's content. Portrait and windows narrower than <see cref="PairedMinimumWidth"/> show
/// the stack as on the iPhone. A wider landscape window shows the stack's root in a 400pt list column and the page the
/// iPhone would push in an item zone beside it, with an idle glyph while nothing is open; Calendar keeps the grid wide
/// and opens event details in a 380pt panel only while one is shown. Rotating moves the pushed pages between the stack
/// and the item zone.
/// </summary>
internal sealed class PadModeContainer : UIViewController
{
    public const double PairedMinimumWidth = 1100;
    public const double ListWidth = 400;
    public const double DetailsWidth = 380;
    private const double Gutter = 12;
    private const double ZoneRadius = 14;

    private readonly ModeStackController _stack;
    private readonly UINavigationController _item;
    private readonly UIViewController? _idle;
    private readonly bool _detailsPanel;
    private readonly Action _changed;
    private bool _paired;
    private CGSize? _transitionSize;
    private UIBarButtonItem? _closeItem;

    public PadModeContainer(ModeStackController stack, WinoApplicationMode mode, IUINavigationControllerDelegate stackDelegate, Action changed)
    {
        _stack = stack;
        _changed = changed;
        _detailsPanel = mode == WinoApplicationMode.Calendar;
        if (!_detailsPanel) _idle = new PadIdleItemViewController(IdleGlyph(mode), mode == WinoApplicationMode.Mail ? Translator.NoMailSelected : string.Empty);
        // A plain navigation controller, so a replaced or closed page releases as a popped one does on the iPhone.
        _item = new UINavigationController();
        _item.SetViewControllers(_idle is null ? [] : [_idle], false);
        _item.Delegate = stackDelegate;
        TabBarItem = stack.TabBarItem;
    }

    public ModeStackController Stack => _stack;

    public bool IsPaired => _paired;

    public void SetIdleTitle(string? title) => (_idle as PadIdleItemViewController)?.SetTitle(title);

    /// <summary>True while the item zone holds a page (not the idle glyph).</summary>
    public bool HasItem => ItemControllers.Length > 0;

    public UIViewController[] ItemControllers => (_item.ViewControllers ?? []).Where(controller => !ReferenceEquals(controller, _idle)).ToArray();

    /// <summary>The navigation controllers that can hold this tab's pages.</summary>
    public IEnumerable<UINavigationController> Navigations => [_stack, _item];

    /// <summary>The list column's frame in this container's coordinates (the stack's whole frame when not paired).</summary>
    public CGRect ListFrame => _stack.View?.Frame ?? CGRect.Empty;

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        AddChildViewController(_stack);
        if (_stack.View is { } stackView) root.AddSubview(stackView);
        _stack.DidMoveToParentViewController(this);
        AddChildViewController(_item);
        if (_item.View is { } itemView)
        {
            itemView.BackgroundColor = WinoStyle.ZoneFill;
            itemView.Layer.CornerRadius = (nfloat)ZoneRadius;
            itemView.Layer.MasksToBounds = true;
            itemView.Hidden = true;
            root.AddSubview(itemView);
        }
        _item.DidMoveToParentViewController(this);
        var appearance = new UINavigationBarAppearance();
        appearance.ConfigureWithTransparentBackground();
        _item.NavigationBar.StandardAppearance = appearance;
        _item.NavigationBar.ScrollEdgeAppearance = appearance;
        View = root;
    }

    public override void ViewWillLayoutSubviews()
    {
        base.ViewWillLayoutSubviews();
        if (View is not { } root) return;
        var bounds = root.Bounds;
        // During a rotation a layout pass can still see the old size. Flipping the pairing back and forth then makes UIKit
        // defer a stack change and leaves the open page on the wrong side, so the target size decides until it ends.
        SetPaired(Pairs(_transitionSize ?? bounds.Size));
        if (_transitionSize is null) MoveStrandedPages();
        LayoutColumns(bounds, root.SafeAreaInsets);
    }

    public override void ViewWillTransitionToSize(CGSize toSize, IUIViewControllerTransitionCoordinator coordinator)
    {
        base.ViewWillTransitionToSize(toSize, coordinator);
        _transitionSize = toSize;
        SetPaired(Pairs(toSize));
        coordinator.AnimateAlongsideTransition(_ => View?.SetNeedsLayout(), _ =>
        {
            _transitionSize = null;
            if (View is not { } root) return;
            SetPaired(Pairs(root.Bounds.Size));
            MoveStrandedPages();
            LayoutColumns(root.Bounds, root.SafeAreaInsets);
        });
    }

    // While paired, pushes from the root go to the item zone, so pages above the root can only be left from a deferred
    // stack change; they belong beside the list.
    private void MoveStrandedPages()
    {
        if (!_paired || _stack.ViewControllers is not [var root, .. var stranded] || stranded.Length == 0) return;
        foreach (var controller in stranded) WinoPageMoves.Begin(controller);
        _stack.PushDirect([root]);
        PlaceItems(stranded);
        _changed();
    }

    private static bool Pairs(CGSize size) => size.Width >= PairedMinimumWidth && size.Width > size.Height;

#if DEBUG
    internal void DebugClose() => CloseItemByUser();

    internal string DebugDescribe()
        => $"paired={_paired} bounds={View?.Bounds.Size} list={_stack.View?.Frame} item={(_item.View is { Hidden: false } itemView ? itemView.Frame.ToString() : "hidden")} " +
           $"stack=[{string.Join(",", (_stack.ViewControllers ?? []).Select(static c => c.GetType().Name))}] items=[{string.Join(",", ItemControllers.Select(static c => c.GetType().Name))}]";
#endif

    private void LayoutColumns(CGRect bounds, UIEdgeInsets safe)
    {
        if (_stack.View is not { } stackView || _item.View is not { } itemView) return;
        bool showItem = _paired && (!_detailsPanel || HasItem);
        if (!showItem)
        {
            stackView.Frame = bounds;
            itemView.Hidden = true;
            return;
        }
        double itemWidth = _detailsPanel ? DetailsWidth : (double)bounds.Width - safe.Left - ListWidth - Gutter - safe.Right - Gutter;
        double listWidth = _detailsPanel ? (double)bounds.Width - safe.Right - Gutter - DetailsWidth - Gutter : safe.Left + ListWidth;
        stackView.Frame = new CGRect(0, 0, listWidth, bounds.Height);
        double top = (double)safe.Top;
        double bottom = (double)(bounds.Height - safe.Bottom) - Gutter;
        itemView.Frame = new CGRect(listWidth + Gutter, top, itemWidth, Math.Max(0, bottom - top));
        itemView.Hidden = false;
        UpdateItemBar();
    }

    private void SetPaired(bool paired)
    {
        if (_paired == paired) return;
        _paired = paired;
        _stack.Pairing = this;
        var moving = paired ? (_stack.ViewControllers ?? []).Skip(1).ToArray() : ItemControllers;
        foreach (var controller in moving) WinoPageMoves.Begin(controller);
        if (paired)
        {
            if (moving.Length > 0 && _stack.ViewControllers is [var root, ..])
            {
                _stack.PushDirect([root]);
                PlaceItems(moving);
            }
        }
        else
        {
            PlaceItems([]);
            if (moving.Length > 0 && _stack.ViewControllers is { Length: > 0 } stack) _stack.PushDirect([.. stack, .. moving]);
        }
        _stack.SetToolbarHidden(_stack.TopViewController?.ToolbarItems is not { Length: > 0 }, false);
        if (_stack.ViewControllers is [IPadPairingObserver observer, ..]) observer.PadPairingChanged();
        _changed();
    }

    /// <summary>Shows <paramref name="controller"/> in the item zone, in place of the open item.</summary>
    internal void ShowItem(UIViewController controller)
    {
        var previous = ItemControllers;
        foreach (var old in previous) if (!ReferenceEquals(old, controller)) ReleaseItem(old);
        PlaceItems([controller]);
        if (View is { } root) LayoutColumns(root.Bounds, root.SafeAreaInsets);
        _changed();
    }

    /// <summary>Closes the open item: the idle glyph, or no panel in Calendar.</summary>
    internal UIViewController[] CloseItem()
    {
        var closed = ItemControllers;
        PlaceItems([]);
        if (View is { } root) LayoutColumns(root.Bounds, root.SafeAreaInsets);
        _changed();
        return closed;
    }

    /// <summary>Closes the open item for good (its list page is being replaced) and releases it.</summary>
    internal UIViewController[] DiscardItem()
    {
        var closed = CloseItem();
        foreach (var page in closed) ReleaseItem(page);
        return closed;
    }

    private void CloseItemByUser()
    {
        CloseItem();
        if (_stack.ViewControllers is [IPadItemOwner owner, ..]) owner.PadItemClosed();
    }

    private void PlaceItems(UIViewController[] controllers)
    {
        // The details panel has no Back; its close button does what Back does on the iPhone. It leaves with the page, so a
        // page that moves back to the stack gets its Back button again.
        foreach (var gone in ItemControllers)
            if (ReferenceEquals(gone.NavigationItem.LeftBarButtonItem, _closeItem) && Array.IndexOf(controllers, gone) != 0) gone.NavigationItem.LeftBarButtonItem = null;
        if (_detailsPanel && controllers is [var first, ..] && first.NavigationItem.LeftBarButtonItem is null)
        {
            _closeItem ??= new UIBarButtonItem(WinoIcons.TemplateImage(WinoIconGlyph.Dismiss, 18), UIBarButtonItemStyle.Plain, (_, _) => CloseItemByUser())
            {
                AccessibilityLabel = Translator.Buttons_Close
            };
            first.NavigationItem.LeftBarButtonItem = _closeItem;
        }
        UIViewController[] next = controllers.Length > 0 ? controllers : _idle is null ? [] : [_idle];
        _item.SetViewControllers(next, false);
        _item.SetToolbarHidden(_item.TopViewController?.ToolbarItems is not { Length: > 0 }, false);
        UpdateItemBar();
    }

    // The item zone shows a bar only for pages that put buttons in it; the reader and the details keep theirs in the page.
    private void UpdateItemBar()
    {
        var top = _item.TopViewController;
        bool hasItems = top?.NavigationItem is { } item && ((item.LeftBarButtonItems?.Length ?? 0) > 0 || (item.RightBarButtonItems?.Length ?? 0) > 0);
        _item.SetNavigationBarHidden(!(hasItems || (_item.ViewControllers?.Length ?? 0) > 1), false);
    }

    // A replaced page left the item zone for good; the iPhone stack releases it when it is popped.
    private static void ReleaseItem(UIViewController controller)
    {
        switch (controller)
        {
            case Wino.Mail.UIKit.Views.Contacts.ContactCardViewController card: card.Release(); break;
            case Wino.Mail.UIKit.Views.ToDo.ToDoTaskDetailViewController detail: detail.Release(); break;
        }
    }

    private static WinoIconGlyph IdleGlyph(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Contacts => WinoIconGlyph.PeopleFilled,
        WinoApplicationMode.Tasks => WinoIconGlyph.CheckmarkCircleFilled,
        _ => WinoIconGlyph.MailFilled
    };
}

/// <summary>A list page told when the user closes the item beside it (the iPhone pop that has no Back here).</summary>
internal interface IPadItemOwner
{
    void PadItemClosed();
}

/// <summary>A list page whose bar depends on whether the item zone is beside it.</summary>
internal interface IPadPairingObserver
{
    void PadPairingChanged();
}

/// <summary>The item zone's empty state: the mode glyph, and "No message selected" in Mail.</summary>
internal sealed class PadIdleItemViewController(WinoIconGlyph glyph, string title) : UIViewController
{
    private WinoStateView? _state;
    private string? _override;

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = WinoStyle.ZoneFill };
        _state = new WinoStateView(glyph, _override ?? title, null, WinoStyle.TertiaryText);
        _state.Place(root);
        View = root;
    }

    /// <summary>Replaces the idle text (the Mail selection count), or restores it with null.</summary>
    public void SetTitle(string? text)
    {
        _override = text;
        if (_state is not null) _state.Title.Text = text ?? title;
    }
}
