using CoreGraphics;
using ObjCRuntime;
using UIKit;
using WebKit;

namespace Wino.Mail.UIKit.Views.Mail.Compose;

/// <summary>
/// The composer body: a transparent WKWebView that the page hands to <c>WebKitHtmlMailEditorSession</c>. WKWebView has
/// no settable keyboard accessory, but its content view asks the web view for <see cref="InputAccessoryView"/>, so
/// overriding it puts the formatting toolbar above the keyboard while the body has focus.
/// </summary>
internal sealed class ComposeEditorWebView : WKWebView
{
    private UIView? _accessory;

    public ComposeEditorWebView() : base(CGRect.Empty, CreateConfiguration())
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Opaque = false;
        BackgroundColor = UIColor.Clear;
        ScrollView.BackgroundColor = UIColor.Clear;
        ScrollView.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive;
#if DEBUG
        Inspectable = true;
#endif
    }

    public ComposeEditorWebView(NativeHandle handle) : base(handle)
    {
    }

    private static WKWebViewConfiguration CreateConfiguration()
        => new() { DataDetectorTypes = WKDataDetectorTypes.None };

    /// <summary>The view shown above the keyboard while the body is being edited; null shows none.</summary>
    public UIView? Accessory
    {
        get => _accessory;
        set
        {
            if (ReferenceEquals(_accessory, value)) return;
            _accessory = value;
            ReloadInputViews();
        }
    }

    /// <summary>The formatting bar, or WebKit's own (none) when no accessory is set.</summary>
    public override UIView InputAccessoryView => _accessory ?? base.InputAccessoryView;
}
