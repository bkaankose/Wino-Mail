using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>Connection state next to a server section (Windows ConnectionStatusPill): a coloured dot, or a spinner while a test runs, and the state text.</summary>
internal sealed class ConnectionStatusView
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
    private readonly Gtk.Label _dot = Gtk.Label.New("●");
    private readonly Adw.Spinner _spinner = OnboardingUi.Spinner();
    private readonly Gtk.Label _text = Gtk.Label.New(string.Empty);
    private ConnectionTestState? _state;

    public ConnectionStatusView()
    {
        _root.SetValign(Gtk.Align.Center);
        _dot.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _text.AddCssClass("caption");
        _text.AddCssClass("dim-label");
        _root.Append(_dot);
        _root.Append(_spinner);
        _root.Append(_text);
        Apply(ConnectionTestState.NotTested);
    }

    public Gtk.Widget Root => _root;

    public void Apply(ConnectionTestState state)
    {
        if (_state == state) return;
        _state = state;
        _text.SetText(state switch
        {
            ConnectionTestState.Testing => Translator.ImapSetup_StatusTesting,
            ConnectionTestState.Succeeded => Translator.ImapSetup_StatusConnected,
            ConnectionTestState.Failed => Translator.ImapSetup_StatusFailed,
            _ => Translator.ImapSetup_StatusNotTested,
        });
        foreach (var name in new[] { "success", "error", "dim-label" }) _dot.RemoveCssClass(name);
        _dot.AddCssClass(state switch
        {
            ConnectionTestState.Succeeded => "success",
            ConnectionTestState.Failed => "error",
            _ => "dim-label",
        });
        var testing = state == ConnectionTestState.Testing;
        _dot.SetVisible(!testing);
        _spinner.SetVisible(testing);
        WinoA11y.Label(_root, _text.GetText());
    }
}

/// <summary>
/// The add-account steps on the server page: Features (done on the setup page), Sign in and Servers. The current step
/// has an accent number, a finished one a check mark.
/// </summary>
internal sealed class SetupStepIndicator
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Step _signIn;
    private readonly Step _servers;

    public SetupStepIndicator()
    {
        _root.SetHalign(Gtk.Align.Center);
        var features = new Step("1", Translator.ImapSetup_StepFeatures);
        _signIn = new Step("2", Translator.ImapSetup_StepSignIn);
        _servers = new Step("3", Translator.ImapSetup_StepServers);
        features.Apply(done: true, current: false);
        _root.Append(features.Root);
        _root.Append(Connector());
        _root.Append(_signIn.Root);
        _root.Append(Connector());
        _root.Append(_servers.Root);
        SetCurrent(ImapSetupStep.SignIn);
    }

    public Gtk.Widget Root => _root;

    public void SetCurrent(ImapSetupStep step)
    {
        _signIn.Apply(done: step == ImapSetupStep.Servers, current: step == ImapSetupStep.SignIn);
        _servers.Apply(done: false, current: step == ImapSetupStep.Servers);
    }

    private static Gtk.Widget Connector()
    {
        var line = Gtk.Separator.New(Gtk.Orientation.Horizontal);
        line.SetSizeRequest(24, -1);
        line.SetValign(Gtk.Align.Center);
        return line;
    }

    private sealed class Step
    {
        private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        private readonly Gtk.Label _circle;
        private readonly string _number;
        private readonly Gtk.Label _title;

        public Step(string number, string title)
        {
            _number = number;
            _circle = Gtk.Label.New(number);
            _circle.AddCssClass("wino-setup-step-number");
            _circle.SetSizeRequest(22, 22);
            _title = Gtk.Label.New(title);
            _root.Append(_circle);
            _root.Append(_title);
            WinoA11y.Label(_root, title);
        }

        public Gtk.Widget Root => _root;

        public void Apply(bool done, bool current)
        {
            _circle.SetText(done ? "✓" : _number);
            if (done || current) _circle.AddCssClass("wino-setup-step-accent");
            else _circle.RemoveCssClass("wino-setup-step-accent");
            if (current)
            {
                _title.AddCssClass("heading");
                _title.RemoveCssClass("dim-label");
            }
            else
            {
                _title.RemoveCssClass("heading");
                _title.AddCssClass("dim-label");
            }
        }
    }
}
