using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Dialogs;

/// <summary>
/// The signature editor (Windows SignatureEditorDialog, the Mac sheet) as a large navigation sheet: Cancel and Save in
/// the bar, the account line, the name field, then the formatting toolbar over the composer's HTML editor session,
/// which takes the remaining height above the keyboard, and the images hint. Save stays disabled while the name is
/// empty or the editor is still loading. Only Cancel closes it, so a swipe never discards an edit.
/// Build on the main thread, then call <see cref="PresentAsync"/> once.
/// </summary>
internal sealed class SignatureEditorSheet
{
    private readonly EditorController _controller;
    private readonly UITextField _name;
    private readonly UIBarButtonItem _save;
    private readonly UIBarButtonItem _cancel;
    private readonly UIActivityIndicatorView _spinner = new(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true };
    private readonly WinoRichTextEditorView _editor;
    private readonly string _html;
    private readonly Action<Exception>? _error;
    private TaskCompletionSource<(string Name, string HtmlBody)?>? _completion;
    private bool _busy;
    private bool _presented;
    private bool _disposed;

    public SignatureEditorSheet(string? accountLine, string? name, string? html, IExternalLauncher? launcher, IPreferencesService? preferences, Action<Exception>? error)
    {
        _html = html ?? string.Empty;
        _error = error;

        // ---- Account line ----
        var account = WinoStyle.Label(accountLine, WinoStyle.Subheadline, WinoStyle.SecondaryText);
        account.Hidden = string.IsNullOrWhiteSpace(accountLine);

        // ---- Name ----
        var nameCaption = WinoStyle.Label(Translator.SignatureEditorDialog_SignatureName_TitleNew, WinoStyle.Subheadline, WinoStyle.SecondaryText);
        nameCaption.IsAccessibilityElement = false;
        _name = new UITextField
        {
            Text = name?.Trim() ?? string.Empty,
            Placeholder = Translator.SignatureEditorDialog_SignatureName_Placeholder,
            BorderStyle = UITextBorderStyle.RoundedRect,
            ClearButtonMode = UITextFieldViewMode.WhileEditing,
            Font = WinoStyle.Body,
            AdjustsFontForContentSizeCategory = true,
            ReturnKeyType = UIReturnKeyType.Next,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _name.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoAccessibility.Label(_name, Translator.SignatureEditorDialog_SignatureName_TitleNew);
        _name.EditingChanged += (_, _) => Refresh();
        var nameStack = WinoLayout.VStack(WinoStyle.Space1, nameCaption, _name);
        nameStack.Alignment = UIStackViewAlignment.Fill;

        // ---- Format toolbar and editor (the composer's HTML editor session) ----
        _editor = new WinoRichTextEditorView(Translator.SettingsSignature_Title, launcher, preferences, error);
        _editor.EditorSurface.HeightAnchor.ConstraintGreaterThanOrEqualTo(160).Active = true;
        _editor.ReadyChanged += (_, _) => Refresh();
        _editor.SetContentHuggingPriority(1, UILayoutConstraintAxis.Vertical);
        _name.ShouldReturn = _ =>
        {
            Observe(_editor.FocusAsync());
            return false;
        };

        // ---- Hint ----
        var hint = WinoStyle.Label(Translator.SignatureEditorDialog_ImagesHint, WinoStyle.Caption, WinoStyle.TertiaryText, 0);

        var stack = WinoLayout.VStack(14, account, nameStack, _editor, hint);
        stack.Alignment = UIStackViewAlignment.Fill;
        _controller = new EditorController(stack) { Title = Translator.SignatureEditorDialog_Title, ModalInPresentation = true };

        _cancel = new UIBarButtonItem(Translator.Buttons_Cancel, UIBarButtonItemStyle.Plain, (_, _) => Finish(null));
        _save = new UIBarButtonItem(Translator.Buttons_Save, UIBarButtonItemStyle.Done, (_, _) => Save());
        _controller.NavigationItem.LeftBarButtonItem = _cancel;
        _controller.NavigationItem.RightBarButtonItem = _save;
        Refresh();
    }

    /// <summary>Shows the sheet; returns the trimmed name and the editor HTML, or null when cancelled.</summary>
    public Task<(string Name, string HtmlBody)?> PresentAsync(UIViewController presenter)
    {
        if (_presented) throw new InvalidOperationException("The signature editor is presented once.");
        _presented = true;
        _completion = new TaskCompletionSource<(string Name, string HtmlBody)?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var navigation = new UINavigationController(_controller)
        {
            ModalPresentationStyle = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad ? UIModalPresentationStyle.FormSheet : UIModalPresentationStyle.PageSheet,
            ModalInPresentation = true
        };
        if (navigation.SheetPresentationController is { } sheet)
            sheet.Detents = [UISheetPresentationControllerDetent.CreateLargeDetent()];
        if (UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad)
            navigation.PreferredContentSize = new CoreGraphics.CGSize(640, 620);

        presenter.PresentViewController(navigation, true, () =>
        {
            Observe(_editor.LoadAsync(_html, WinoTraits.IsDark(_controller.TraitCollection)));
            if (string.IsNullOrEmpty(_name.Text)) _name.BecomeFirstResponder();
        });
        return _completion.Task;
    }

    private bool CanSave => _editor.IsReady && !_busy && !string.IsNullOrWhiteSpace(_name.Text);

    private void Refresh()
    {
        _save.Enabled = CanSave;
        _cancel.Enabled = !_busy;
    }

    private async void Save()
    {
        if (!CanSave) return;
        _busy = true;
        _controller.NavigationItem.RightBarButtonItem = new UIBarButtonItem(_spinner);
        _spinner.StartAnimating();
        Refresh();
        try
        {
            var html = await _editor.GetHtmlBodyAsync() ?? string.Empty;
            Finish(((_name.Text ?? string.Empty).Trim(), html));
        }
        catch (Exception exception)
        {
            _error?.Invoke(exception);
            _busy = false;
            _spinner.StopAnimating();
            _controller.NavigationItem.RightBarButtonItem = _save;
            Refresh();
        }
    }

    private void Finish((string Name, string HtmlBody)? result)
    {
        if (_completion is null || !_completion.TrySetResult(result)) return;
        _controller.View?.EndEditing(true);
        _controller.DismissViewController(true, () => Observe(DisposeAsync()));
    }

    private async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _editor.DisposeAsync();
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _error?.Invoke(exception); }
    }

    /// <summary>The page: the form pinned between the safe area and the keyboard, so the editor shrinks above it.</summary>
    private sealed class EditorController(UIView content) : UIViewController
    {
        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            if (View is not { } root) return;
            root.BackgroundColor = UIColor.SystemGroupedBackground;
            content.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(content);
            var guide = root.SafeAreaLayoutGuide;
            NSLayoutConstraint.ActivateConstraints(
            [
                content.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 16),
                content.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, 20),
                content.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -20),
                content.BottomAnchor.ConstraintEqualTo(root.KeyboardLayoutGuide.TopAnchor, -16)
            ]);
        }
    }
}
