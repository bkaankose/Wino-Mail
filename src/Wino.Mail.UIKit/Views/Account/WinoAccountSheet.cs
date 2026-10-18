using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Account;

/// <summary>
/// Base for the Wino Account sheets (the Mac WinoAccountSheet, port of the Windows ContentDialogs): a navigation sheet
/// (page sheet on iPhone, form sheet on iPad) with Cancel on the leading side and the consequence-led primary button
/// on the trailing side, the rows in the Windows field order, an inline error line under them and a spinner in place
/// of the primary button while work runs. Return moves to the next visible field and runs the primary action from the
/// last one; swiping the sheet away cancels. Build on the main thread, then call <see cref="PresentAsync"/> once.
/// </summary>
internal abstract class WinoAccountSheet<TResult>
{
    private readonly TaskCompletionSource<TResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SheetController _controller;
    private readonly UILabel _error;
    private readonly UIStackView _content;
    private readonly UIActivityIndicatorView _spinner = new(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true };
    private readonly List<UITextField> _fields = [];
    private readonly DismissalDelegate _dismissal;
    private bool _busy;
    private bool _presented;
    private bool _finished;

    protected WinoAccountSheet(string title)
    {
        Body = WinoLayout.VStack(20);
        Body.Alignment = UIStackViewAlignment.Fill;
        _error = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.Critical, 0);
        _error.Hidden = true;
        _content = WinoLayout.VStack(12, Body, _error);
        _content.Alignment = UIStackViewAlignment.Fill;

        _controller = new SheetController(_content, this) { Title = title };
        _dismissal = new DismissalDelegate(this);
        Secondary = new UIBarButtonItem(Translator.Buttons_Cancel, UIBarButtonItemStyle.Plain, (_, _) => { if (!_busy) Finish(default); });
        Primary = new UIBarButtonItem(Translator.Buttons_OK, UIBarButtonItemStyle.Done, (_, _) => RunPrimary());
        _controller.NavigationItem.LeftBarButtonItem = Secondary;
        _controller.NavigationItem.RightBarButtonItem = Primary;
    }

    /// <summary>The rows, in order; <see cref="AddRow"/> appends to it.</summary>
    protected UIStackView Body { get; }
    protected UIBarButtonItem Primary { get; }
    protected UIBarButtonItem Secondary { get; }
    protected UIViewController Controller => _controller;
    protected bool IsBusy => _busy;

    protected string Title
    {
        get => _controller.Title ?? string.Empty;
        set => _controller.Title = value;
    }

    /// <summary>Runs for the primary button (or Return in the last field) when the sheet is not busy and <see cref="CanSubmit"/>.</summary>
    protected abstract Task PrimaryAsync();

    protected virtual bool CanSubmit => true;
    protected virtual UIView? InitialResponder => _fields.FirstOrDefault(IsShown);
    protected virtual void Opened() { }
    protected virtual void Closed() { }

    /// <summary>Runs after any field edit, after the primary button state was refreshed.</summary>
    protected virtual void FieldChanged() => ShowError(null);

    protected void AddRow(UIView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        Body.AddArrangedSubview(view);
    }

    public Task<TResult?> PresentAsync(UIViewController presenter)
    {
        if (_presented) throw new InvalidOperationException("A Wino Account sheet is presented once.");
        _presented = true;
        var navigation = new UINavigationController(_controller)
        {
            ModalPresentationStyle = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad ? UIModalPresentationStyle.FormSheet : UIModalPresentationStyle.PageSheet
        };
        if (navigation.SheetPresentationController is { } sheet)
        {
            sheet.Detents = [UISheetPresentationControllerDetent.CreateLargeDetent()];
            sheet.PrefersScrollingExpandsWhenScrolledToEdge = true;
        }
        if (navigation.PresentationController is { } presentation) presentation.Delegate = _dismissal;
        Refresh();
        presenter.PresentViewController(navigation, true, () =>
        {
            InitialResponder?.BecomeFirstResponder();
            Opened();
        });
        return _completion.Task;
    }

    protected void Finish(TResult? result) => Finish(result, dismiss: true);

    /// <summary>
    /// Completes once the sheet is gone, so a follow-up sheet or alert (the email confirmation step) presents from the
    /// controller underneath instead of from a sheet that is still animating away.
    /// </summary>
    private void Finish(TResult? result, bool dismiss)
    {
        if (_finished) return;
        _finished = true;
        Closed();
        if (!dismiss)
        {
            _completion.TrySetResult(result);
            return;
        }
        _controller.View?.EndEditing(true);
        _controller.DismissViewController(true, () => _completion.TrySetResult(result));
    }

    /// <summary>Enables the primary button from <see cref="CanSubmit"/> and the busy state.</summary>
    protected void Refresh() => Primary.Enabled = !_busy && CanSubmit;

    /// <summary>Windows SetBusyState: fields and both buttons off, a spinner in place of the primary button.</summary>
    protected void SetBusy(bool busy)
    {
        if (_finished) return;
        _busy = busy;
        Body.UserInteractionEnabled = !busy;
        Body.Alpha = busy ? 0.6f : 1f;
        _controller.ModalInPresentation = busy;
        Secondary.Enabled = !busy;
        if (busy)
        {
            _controller.View?.EndEditing(true);
            _spinner.StartAnimating();
        }
        else _spinner.StopAnimating();
        _controller.NavigationItem.RightBarButtonItem = busy ? new UIBarButtonItem(_spinner) : Primary;
        Refresh();
    }

    /// <summary>The inline error line under the rows; null hides it. VoiceOver announces a new message.</summary>
    protected void ShowError(string? message)
    {
        var changed = _error.Text != (message ?? string.Empty);
        _error.Text = message ?? string.Empty;
        _error.Hidden = string.IsNullOrEmpty(message);
        if (!_error.Hidden && changed) UIAccessibility.PostNotification(UIAccessibilityPostNotification.Announcement, new NSString(_error.Text));
    }

    private async void RunPrimary()
    {
        if (_busy || _finished || !CanSubmit) return;
        try { await PrimaryAsync(); }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "A Wino Account sheet action failed.");
            SetBusy(false);
            ShowError(exception.Message);
        }
    }

    private bool ReturnPressed(UITextField field)
    {
        var shown = _fields.Where(IsShown).ToList();
        int index = shown.IndexOf(field);
        if (index >= 0 && index < shown.Count - 1)
        {
            shown[index + 1].BecomeFirstResponder();
            return false;
        }
        RunPrimary();
        return false;
    }

    /// <summary>False when the field or one of its rows is hidden (the password row in forgot-password mode).</summary>
    private bool IsShown(UIView view)
    {
        for (UIView? current = view; current is not null && current != _content; current = current.Superview)
            if (current.Hidden) return false;
        return true;
    }

    // ---- Row builders ----

    /// <summary>The Wino mark and the centred description (the title is the navigation bar title).</summary>
    protected static UIStackView Header(string description, out UILabel descriptionLabel, bool mark = true)
    {
        var icon = new UIImageView(UIImage.FromBundle("WinoLogo") ?? UIImage.FromBundle("AppIcon")) { ContentMode = UIViewContentMode.ScaleAspectFit, IsAccessibilityElement = false };
        WinoLayout.Size(icon, 56, 56);
        icon.Hidden = !mark || icon.Image is null;
        descriptionLabel = WinoStyle.Label(description, WinoStyle.Subheadline, WinoStyle.SecondaryText, 0);
        descriptionLabel.TextAlignment = UITextAlignment.Center;
        var stack = WinoLayout.VStack(12, icon, descriptionLabel);
        stack.Alignment = UIStackViewAlignment.Center;
        return stack;
    }

    /// <summary>A caption above a rounded text field (or a secure field); the field joins the Return order.</summary>
    protected UIStackView Field(string label, out UITextField field, string? placeholder = null, bool secure = false)
    {
        field = new UITextField
        {
            Placeholder = placeholder ?? string.Empty,
            SecureTextEntry = secure,
            BorderStyle = UITextBorderStyle.RoundedRect,
            ClearButtonMode = secure ? UITextFieldViewMode.Never : UITextFieldViewMode.WhileEditing,
            Font = WinoStyle.Body,
            AdjustsFontForContentSizeCategory = true,
            AutocorrectionType = UITextAutocorrectionType.No,
            SpellCheckingType = UITextSpellCheckingType.No,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        field.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoAccessibility.Label(field, label);
        field.EditingChanged += (_, _) => { Refresh(); FieldChanged(); };
        field.ShouldReturn = ReturnPressed;
        _fields.Add(field);
        var caption = WinoStyle.Label(label, WinoStyle.Subheadline, WinoStyle.PrimaryText, 0);
        caption.IsAccessibilityElement = false;
        var stack = WinoLayout.VStack(6, caption, field);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    /// <summary>An email field: email keyboard and autofill, no capitalisation.</summary>
    protected static void EmailField(UITextField field)
    {
        field.KeyboardType = UIKeyboardType.EmailAddress;
        field.AutocapitalizationType = UITextAutocapitalizationType.None;
        field.TextContentType = UITextContentType.EmailAddress;
    }

    /// <summary>A leading-aligned accent text button (Windows HyperlinkButton).</summary>
    protected static UIButton Link(string title, Action action)
    {
        var button = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false, HorizontalAlignment = UIControlContentHorizontalAlignment.Leading };
        SetLinkTitle(button, title);
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }

    protected static void SetLinkTitle(UIButton button, string title)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = title;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
        configuration.BaseForegroundColor = WinoStyle.Accent;
        button.Configuration = configuration;
    }

    /// <summary>The scrolling page inside the sheet.</summary>
    private sealed class SheetController(UIView content, WinoAccountSheet<TResult> owner) : UIViewController
    {
        // The controller keeps its sheet object alive while it is on screen.
        public WinoAccountSheet<TResult> Owner { get; } = owner;

        public override void LoadView()
        {
            // The controller sizes its root view; it keeps its autoresizing mask. The scroll view ends at the
            // keyboard, so the fields and the sign-in buttons below them stay reachable while it is up.
            var root = new UIView { BackgroundColor = UIColor.SystemGroupedBackground };
            var scroll = new UIScrollView
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                AlwaysBounceVertical = true,
                KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive
            };
            content.TranslatesAutoresizingMaskIntoConstraints = false;
            scroll.AddSubview(content);
            root.AddSubview(scroll);
            var frame = scroll.FrameLayoutGuide;
            var guide = scroll.ContentLayoutGuide;
            var width = content.WidthAnchor.ConstraintEqualTo(frame.WidthAnchor, 1, -40);
            width.Priority = 750;
            NSLayoutConstraint.ActivateConstraints(
            [
                scroll.TopAnchor.ConstraintEqualTo(root.TopAnchor),
                scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
                scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
                scroll.BottomAnchor.ConstraintEqualTo(root.KeyboardLayoutGuide.TopAnchor),
                content.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 20),
                content.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -24),
                content.CenterXAnchor.ConstraintEqualTo(frame.CenterXAnchor),
                content.WidthAnchor.ConstraintLessThanOrEqualTo(520),
                content.LeadingAnchor.ConstraintGreaterThanOrEqualTo(frame.LeadingAnchor, 20),
                width
            ]);
            View = root;
        }
    }

    /// <summary>Swiping the sheet away cancels.</summary>
    private sealed class DismissalDelegate(WinoAccountSheet<TResult> owner) : UIAdaptivePresentationControllerDelegate
    {
        public override void DidDismiss(UIPresentationController presentationController) => owner.Finish(default, dismiss: false);
    }
}
