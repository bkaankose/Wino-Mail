using Wino.Core.Domain;
using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Shown for Settings pages that have no native implementation yet, and when a page fails to open.
/// The window keeps its sidebar, history and title, so nothing silently disappears.
/// </summary>
public sealed class SettingsPlaceholderViewController : NSViewController, IWinoViewController
{
    public static string LaterMessage => Translator.MacOS_Placeholder_LaterUpdate;
    public static string FailedMessage => Translator.MacOS_Placeholder_PageFailed;

    private readonly WinoPage _page;
    private readonly string _message;

    public SettingsPlaceholderViewController() : this(WinoPage.None) { }

    public SettingsPlaceholderViewController(WinoPage page, string? message = null)
    {
        _page = page;
        _message = message ?? LaterMessage;
    }

    public bool HasPendingWork => false;

    public override void LoadView()
    {
        var circle = new WinoSurfaceView { Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, CornerRadius = 28 };
        WinoLayout.Size(circle, 56, 56);
        var glyph = new WinoIconView(SettingsPageCatalog.Glyph(_page), 26, WinoStyle.SecondaryText);
        circle.AddSubview(glyph);
        NSLayoutConstraint.ActivateConstraints(
        [
            glyph.CenterXAnchor.ConstraintEqualTo(circle.CenterXAnchor),
            glyph.CenterYAnchor.ConstraintEqualTo(circle.CenterYAnchor)
        ]);
        var title = WinoStyle.Label(SettingsPageCatalog.Title(_page), WinoStyle.Heading, WinoStyle.PrimaryText);
        var message = WinoStyle.Label(_message, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        message.Alignment = NSTextAlignment.Center;
        var stack = WinoLayout.VStack(10, circle, title, message);
        stack.Alignment = NSLayoutAttribute.CenterX;
        stack.EdgeInsets = new NSEdgeInsets(60, 0, 0, 0);
        View = SettingsPageScrollView.Create(stack);
    }

    public Task ActivateAsync(NavigationMode mode, object? parameter) => Task.CompletedTask;
    public Task ReleaseAsync() => Task.CompletedTask;
}
