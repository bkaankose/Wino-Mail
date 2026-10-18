using UIKit;
using Wino.Core.Domain;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Dialogs;

/// <summary>
/// A small titled form presented as a sheet (the Mac FormSheet and the Windows ContentDialog): Cancel on the leading
/// side of the navigation bar, the consequence-led primary button on the trailing side, an optional description,
/// labelled rows in the Windows field order (label above the control), an inline error line, and full-width extras.
/// Return moves to the next field and confirms from the last one; swiping the sheet away cancels.
/// Build the form on the main thread, then call <see cref="PresentAsync"/> once.
/// </summary>
public sealed class FormSheet
{
    private readonly FormSheetController _controller;
    private readonly UIStackView _rows = WinoLayout.VStack(WinoStyle.Space4);

    private readonly UIStackView _stack;
    private readonly UILabel _error;
    private readonly UIBarButtonItem _confirm;
    private readonly UIBarButtonItem _cancel;
    private readonly UIActivityIndicatorView _spinner = new(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true };
    private readonly List<UITextField> _fields = [];
    private TaskCompletionSource<bool>? _completion;
    private bool _busy;
    private bool _presented;

    public FormSheet(string title, string? description, string confirmTitle)
    {
        var message = WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        message.Hidden = string.IsNullOrWhiteSpace(description);

        _error = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.Critical, 0);
        _error.Hidden = true;

        _rows.Alignment = UIStackViewAlignment.Fill;
        _stack = WinoLayout.VStack(WinoStyle.Space4, message, _rows, _error);
        _stack.Alignment = UIStackViewAlignment.Fill;

        _controller = new FormSheetController(_stack) { Title = title };
        _cancel = new UIBarButtonItem(Translator.Buttons_Cancel, UIBarButtonItemStyle.Plain, (_, _) => Finish(false));
        _confirm = new UIBarButtonItem(confirmTitle, UIBarButtonItemStyle.Done, (_, _) => _ = RunConfirmAsync());
        _controller.NavigationItem.LeftBarButtonItem = _cancel;
        _controller.NavigationItem.RightBarButtonItem = _confirm;
        _controller.Dismissed = () => Finish(false, dismiss: false);
    }

    /// <summary>Returns an error message to keep the sheet open, or null to accept.</summary>
    public Func<string?>? Validate { get; set; }

    /// <summary>
    /// Runs after <see cref="Validate"/> passes, with the form disabled and a spinner in the bar (a sign-in call).
    /// Return an error message to keep the sheet open, or null to close it confirmed.
    /// </summary>
    public Func<Task<string?>>? ConfirmAsync { get; set; }

    /// <summary>Enables the primary button; re-evaluated after every field edit.</summary>
    public Func<bool>? CanConfirm { get; set; }

    /// <summary>The page inside the sheet (for presenting a nested alert or picker from the form).</summary>
    public UIViewController Controller => _controller;

    /// <summary>The largest detent only (rich editors, long forms). The default lets short forms open at medium height on iPhone.</summary>
    public bool PrefersLargeDetent { get; set; }

    public UITextField AddTextField(string label, string? value, string? placeholder = null, bool secure = false)
    {
        var field = new UITextField
        {
            Text = value ?? string.Empty,
            Placeholder = placeholder ?? string.Empty,
            SecureTextEntry = secure,
            BorderStyle = UITextBorderStyle.RoundedRect,
            ClearButtonMode = UITextFieldViewMode.WhileEditing,
            TranslatesAutoresizingMaskIntoConstraints = false,
            Font = WinoStyle.Body,
            AdjustsFontForContentSizeCategory = true
        };
        if (secure) field.TextContentType = UITextContentType.Password;
        field.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        WinoAccessibility.Label(field, label);
        field.EditingChanged += (_, _) => Refresh();
        field.ShouldReturn = ReturnPressed;
        _fields.Add(field);
        AddRow(label, field);
        return field;
    }

    /// <summary>A pull-down menu button that shows the chosen item (Windows ComboBox).</summary>
    public FormSheetPopUp AddPopUp(string label, IEnumerable<string> items, int selectedIndex, IReadOnlyList<UIImage?>? images = null)
    {
        var popup = new FormSheetPopUp(items.ToList(), selectedIndex, images);
        WinoAccessibility.Label(popup, label);
        popup.Changed += (_, _) => Refresh();
        AddRow(label, popup);
        return popup;
    }

    /// <summary>A switch row with its label on the leading side.</summary>
    public UISwitch AddSwitch(string label, bool value)
    {
        var toggle = new UISwitch { On = value, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(toggle, label);
        toggle.ValueChanged += (_, _) => Refresh();
        var caption = WinoStyle.Label(label, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        var row = WinoLayout.HStack(WinoStyle.Space3, caption, toggle);
        row.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Priorities(toggle, UILayoutConstraintAxis.Horizontal, 750, 1000);
        _rows.AddArrangedSubview(row);
        return toggle;
    }

    /// <summary>A labelled row: the label above the control; a null label shows the control alone.</summary>
    public void AddRow(string? label, UIView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        if (label is null)
        {
            _rows.AddArrangedSubview(view);
            return;
        }
        var caption = WinoStyle.Label(label, WinoStyle.Subheadline, WinoStyle.SecondaryText, 0);
        var row = WinoLayout.VStack(WinoStyle.Space1, caption, view);
        row.Alignment = UIStackViewAlignment.Fill;
        _rows.AddArrangedSubview(row);
    }

    /// <summary>A view under the rows that spans the sheet width (a hint, a preview, an editor).</summary>
    public void AddFullWidth(UIView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _stack.InsertArrangedSubview(view, (nuint)Array.IndexOf(_stack.ArrangedSubviews, _error));
    }

    public void ShowError(string? message)
    {
        _error.Text = message ?? string.Empty;
        _error.Hidden = string.IsNullOrEmpty(message);
        if (!_error.Hidden) UIAccessibility.PostNotification(UIAccessibilityPostNotification.Announcement, new Foundation.NSString(_error.Text));
    }

    public void Refresh() => _confirm.Enabled = !_busy && (CanConfirm?.Invoke() ?? true);

    /// <summary>Disables the form and shows a spinner in place of the primary button while work runs.</summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        _stack.UserInteractionEnabled = !busy;
        _stack.Alpha = busy ? 0.6f : 1f;
        _controller.ModalInPresentation = busy;
        _cancel.Enabled = !busy;
        if (busy) _spinner.StartAnimating();
        else _spinner.StopAnimating();
        _controller.NavigationItem.RightBarButtonItem = busy ? new UIBarButtonItem(_spinner) : _confirm;
        Refresh();
    }

    /// <summary>Closes the sheet with <paramref name="confirmed"/> (a link inside the form that finishes it).</summary>
    public void Close(bool confirmed) => Finish(confirmed);

    public Task<bool> PresentAsync(UIViewController presenter)
    {
        if (_presented) throw new InvalidOperationException("A form sheet is presented once.");
        _presented = true;
        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var navigation = new UINavigationController(_controller)
        {
            ModalPresentationStyle = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad ? UIModalPresentationStyle.FormSheet : UIModalPresentationStyle.PageSheet
        };
        if (navigation.SheetPresentationController is { } sheet)
        {
            sheet.Detents = PrefersLargeDetent
                ? [UISheetPresentationControllerDetent.CreateLargeDetent()]
                : [UISheetPresentationControllerDetent.CreateMediumDetent(), UISheetPresentationControllerDetent.CreateLargeDetent()];
            sheet.PrefersGrabberVisible = !PrefersLargeDetent;
            sheet.PrefersScrollingExpandsWhenScrolledToEdge = true;
        }
        if (navigation.PresentationController is { } presentation) presentation.Delegate = _controller.Dismissal;
        Refresh();
        presenter.PresentViewController(navigation, true, () =>
        {
            if (_fields.FirstOrDefault(field => field.Enabled && string.IsNullOrEmpty(field.Text)) is { } empty) empty.BecomeFirstResponder();
        });
        return _completion.Task;
    }

    private bool ReturnPressed(UITextField field)
    {
        int index = _fields.IndexOf(field);
        if (index >= 0 && index < _fields.Count - 1)
        {
            _fields[index + 1].BecomeFirstResponder();
            return false;
        }
        field.ResignFirstResponder();
        if (_confirm.Enabled) _ = RunConfirmAsync();
        return false;
    }

    private async Task RunConfirmAsync()
    {
        if (_busy || (CanConfirm is not null && !CanConfirm())) return;
        var problem = Validate?.Invoke();
        ShowError(problem);
        if (problem is not null) return;

        if (ConfirmAsync is { } confirm)
        {
            SetBusy(true);
            try { problem = await confirm(); }
            catch (Exception exception)
            {
                Serilog.Log.Warning(exception, "A form sheet's confirm action failed.");
                problem = exception.Message;
            }
            finally { SetBusy(false); }
            ShowError(problem);
            if (problem is not null) return;
        }
        Finish(true);
    }

    private void Finish(bool confirmed, bool dismiss = true)
    {
        if (_completion is null || !_completion.TrySetResult(confirmed)) return;
        if (dismiss) _controller.DismissViewController(true, null);
    }

    /// <summary>The scrolling page inside the sheet; reports a swipe dismissal.</summary>
    private sealed class FormSheetController : UIViewController
    {
        private readonly UIView _content;

        public FormSheetController(UIView content)
        {
            _content = content;
            Dismissal = new DismissalDelegate(this);
        }

        public Action? Dismissed { get; set; }

        public UIAdaptivePresentationControllerDelegate Dismissal { get; }

        public override void LoadView()
        {
            var scroll = new UIScrollView
            {
                BackgroundColor = UIColor.SystemGroupedBackground,
                AlwaysBounceVertical = true,
                KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive
            };
            // The controller sizes its root view; it keeps its autoresizing mask.
            _content.TranslatesAutoresizingMaskIntoConstraints = false;
            scroll.AddSubview(_content);
            var frame = scroll.FrameLayoutGuide;
            var guide = scroll.ContentLayoutGuide;
            var width = _content.WidthAnchor.ConstraintEqualTo(frame.WidthAnchor, 1, -40);
            width.Priority = 750;
            NSLayoutConstraint.ActivateConstraints(
            [
                _content.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 16),
                _content.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -24),
                _content.CenterXAnchor.ConstraintEqualTo(frame.CenterXAnchor),
                _content.WidthAnchor.ConstraintLessThanOrEqualTo(560),
                _content.LeadingAnchor.ConstraintGreaterThanOrEqualTo(frame.LeadingAnchor, 20),
                width
            ]);
            View = scroll;
        }

        private sealed class DismissalDelegate(FormSheetController owner) : UIAdaptivePresentationControllerDelegate
        {
            public override void DidDismiss(UIPresentationController presentationController) => owner.Dismissed?.Invoke();
        }
    }
}

/// <summary>A pull-down menu button that shows the selected item; <see cref="SelectedIndex"/> follows the user's pick.</summary>
public sealed class FormSheetPopUp : UIButton
{
    private readonly List<string> _items;
    private IReadOnlyList<UIImage?>? _images;
    private int _selectedIndex;

    // alloc/init: buttonWithType: returns a plain UIButton whose managed subclass peer the GC can drop.
    public FormSheetPopUp(List<string> items, int selectedIndex, IReadOnlyList<UIImage?>? images = null)
    {
        _items = items;
        _images = images;
        _selectedIndex = selectedIndex >= 0 && selectedIndex < items.Count ? selectedIndex : (items.Count > 0 ? 0 : -1);
        TranslatesAutoresizingMaskIntoConstraints = false;
        ShowsMenuAsPrimaryAction = true;
        HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        Apply();
    }

    public event EventHandler? Changed;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            _selectedIndex = value >= 0 && value < _items.Count ? value : -1;
            Apply();
        }
    }

    public string? SelectedItem => _selectedIndex >= 0 ? _items[_selectedIndex] : null;

    /// <summary>Replaces the menu items (a list that depends on another pick); does not raise <see cref="Changed"/>.</summary>
    public void SetItems(IEnumerable<string> items, int selectedIndex, IReadOnlyList<UIImage?>? images = null)
    {
        _items.Clear();
        _items.AddRange(items);
        _images = images;
        _selectedIndex = selectedIndex >= 0 && selectedIndex < _items.Count ? selectedIndex : (_items.Count > 0 ? 0 : -1);
        Apply();
    }

    private void Apply()
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = SelectedItem ?? string.Empty;
        configuration.Image = _selectedIndex >= 0 && _images is not null && _selectedIndex < _images.Count ? _images[_selectedIndex] : null;
        configuration.ImagePadding = 8;
        configuration.Indicator = UIButtonConfigurationIndicator.Popup;
        Configuration = configuration;

        var actions = new UIMenuElement[_items.Count];
        for (int index = 0; index < _items.Count; index++)
        {
            int chosen = index;
            var image = _images is not null && index < _images.Count ? _images[index] : null;
            var action = UIAction.Create(_items[index], image, null, _ =>
            {
                _selectedIndex = chosen;
                Apply();
                Changed?.Invoke(this, EventArgs.Empty);
            });
            action.State = index == _selectedIndex ? UIMenuElementState.On : UIMenuElementState.Off;
            actions[index] = action;
        }
        Menu = UIMenu.Create(actions);
        AccessibilityValue = SelectedItem;
    }
}
