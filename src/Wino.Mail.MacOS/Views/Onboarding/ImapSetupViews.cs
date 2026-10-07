using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>
/// Connection state next to a server section (Windows ConnectionStatusPill): a coloured dot and
/// the state text, with a small spinner while a test runs.
/// </summary>
internal sealed class ConnectionStatusView : NSStackView
{
    private readonly WinoSurfaceView _dot = new() { CornerRadius = 4 };
    private readonly NSProgressIndicator _spinner = new()
    {
        Style = NSProgressIndicatorStyle.Spinning,
        ControlSize = NSControlSize.Small,
        Indeterminate = true,
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private readonly NSTextField _text = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);

    public ConnectionStatusView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        Spacing = 6;
        Alignment = NSLayoutAttribute.CenterY;
        WinoLayout.Size(_dot, 8, 8);
        WinoLayout.Size(_spinner, 14, 14);
        AddArrangedSubview(_dot);
        AddArrangedSubview(_spinner);
        AddArrangedSubview(_text);
        State = ConnectionTestState.NotTested;
    }

    public ConnectionTestState State
    {
        set
        {
            _text.StringValue = value switch
            {
                ConnectionTestState.Testing => Translator.ImapSetup_StatusTesting,
                ConnectionTestState.Succeeded => Translator.ImapSetup_StatusConnected,
                ConnectionTestState.Failed => Translator.ImapSetup_StatusFailed,
                _ => Translator.ImapSetup_StatusNotTested
            };
            _dot.Fill = value switch
            {
                ConnectionTestState.Succeeded => WinoStyle.Success,
                ConnectionTestState.Failed => WinoStyle.Critical,
                _ => NSColor.TertiaryLabel
            };
            bool testing = value == ConnectionTestState.Testing;
            _dot.Hidden = testing;
            _spinner.Hidden = !testing;
            if (testing) _spinner.StartAnimation(null); else _spinner.StopAnimation(null);
            AccessibilityElement = true;
            AccessibilityLabel = _text.StringValue;
        }
    }
}

/// <summary>
/// The add-account steps on the server page: Features (done on the provider page), Sign in and
/// Servers. The current step has an accent number, a finished one a check mark.
/// </summary>
internal sealed class SetupStepIndicator : NSStackView
{
    private readonly Step _signIn;
    private readonly Step _servers;

    public SetupStepIndicator()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        Spacing = 8;
        Alignment = NSLayoutAttribute.CenterY;
        var features = new Step("1", Translator.ImapSetup_StepFeatures);
        _signIn = new Step("2", Translator.ImapSetup_StepSignIn);
        _servers = new Step("3", Translator.ImapSetup_StepServers);
        features.Apply(done: true, current: false);
        AddArrangedSubview(features);
        AddArrangedSubview(Connector());
        AddArrangedSubview(_signIn);
        AddArrangedSubview(Connector());
        AddArrangedSubview(_servers);
        SetCurrent(ImapSetupStep.SignIn);
    }

    public void SetCurrent(ImapSetupStep step)
    {
        _signIn.Apply(done: step == ImapSetupStep.Servers, current: step == ImapSetupStep.SignIn);
        _servers.Apply(done: false, current: step == ImapSetupStep.Servers);
    }

    private static NSView Connector()
    {
        var line = new WinoSurfaceView { Fill = WinoStyle.Separator };
        WinoLayout.Size(line, 24, 1);
        return line;
    }

    private sealed class Step : NSStackView
    {
        private readonly WinoSurfaceView _circle = new() { CornerRadius = 11 };
        private readonly NSTextField _number;
        private readonly WinoIconView _check = new(WinoIconGlyph.Checkmark, 11, NSColor.White) { Colorful = false };
        private readonly NSTextField _title;

        public Step(string number, string title)
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
            Spacing = 8;
            Alignment = NSLayoutAttribute.CenterY;
            WinoLayout.Size(_circle, 22, 22);
            _number = WinoStyle.Label(number, WinoStyle.CaptionStrong);
            foreach (var view in new NSView[] { _number, _check })
            {
                _circle.AddSubview(view);
                view.CenterXAnchor.ConstraintEqualTo(_circle.CenterXAnchor).Active = true;
                view.CenterYAnchor.ConstraintEqualTo(_circle.CenterYAnchor).Active = true;
            }
            _title = WinoStyle.Label(title, WinoStyle.Body);
            AddArrangedSubview(_circle);
            AddArrangedSubview(_title);
        }

        public void Apply(bool done, bool current)
        {
            bool accent = done || current;
            _circle.Fill = accent ? WinoStyle.Accent : WinoStyle.SubtleFill;
            _number.TextColor = accent ? NSColor.White : WinoStyle.SecondaryText;
            _number.Hidden = done;
            _check.Hidden = !done;
            _title.TextColor = current ? WinoStyle.PrimaryText : WinoStyle.SecondaryText;
            _title.Font = current ? WinoStyle.BodyStrong : WinoStyle.Body;
            AccessibilityElement = true;
            AccessibilityLabel = _title.StringValue;
        }
    }
}

/// <summary>
/// Accessory of the IMAP validation-failed alert (Windows ImapValidationFailedDialog): the
/// "Protocol log" heading over a selectable, monospaced log that scrolls both ways.
/// </summary>
internal static class ImapValidationLogView
{
    public static NSView Create(string protocolLog)
    {
        var heading = WinoStyle.Label(Translator.ImapValidationFailedDialog_ProtocolLog, WinoStyle.BodyStrong);
        var text = new NSTextView(new CoreGraphics.CGRect(0, 0, 520, 240))
        {
            Editable = false,
            Selectable = true,
            RichText = false,
            Font = NSFont.MonospacedSystemFont(11, NSFontWeight.Regular),
            TextColor = NSColor.Label,
            DrawsBackground = false,
            HorizontallyResizable = true,
            VerticallyResizable = true,
            MaxSize = new CoreGraphics.CGSize(float.MaxValue, float.MaxValue),
            Value = protocolLog ?? string.Empty
        };
        text.TextContainer!.WidthTracksTextView = false;
        text.TextContainer.Size = new CoreGraphics.CGSize(float.MaxValue, float.MaxValue);
        text.TextContainerInset = new CoreGraphics.CGSize(6, 6);
        text.AccessibilityLabel = Translator.ImapValidationFailedDialog_ProtocolLog;
        var scroll = new NSScrollView(new CoreGraphics.CGRect(0, 0, 520, 240))
        {
            HasVerticalScroller = true,
            HasHorizontalScroller = true,
            AutohidesScrollers = true,
            BorderType = NSBorderType.BezelBorder,
            DocumentView = text,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(scroll, 520, 240);
        var stack = WinoLayout.VStack(6, heading, scroll);
        stack.Alignment = NSLayoutAttribute.Leading;
        // NSAlert sizes its accessory from the frame, so give the stack its fitted size.
        stack.Frame = new CoreGraphics.CGRect(0, 0, 520, 240 + 6 + heading.FittingSize.Height);
        stack.TranslatesAutoresizingMaskIntoConstraints = true;
        return stack;
    }
}
