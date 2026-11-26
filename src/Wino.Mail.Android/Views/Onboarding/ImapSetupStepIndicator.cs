using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// The add-account steps on the server page: Features, Sign in and Servers (Windows ImapSetupStepper). Features is
/// current until the apps are chosen and done after it; the current step has an accent number, a finished one a check
/// mark.
/// </summary>
internal sealed class ImapSetupStepIndicator : LinearLayout
{
    private readonly StepView _features;
    private readonly StepView _signIn;
    private readonly StepView _servers;

    public ImapSetupStepIndicator(Context context) : base(context)
    {
        Orientation = Orientation.Horizontal;
        SetGravity(GravityFlags.CenterVertical);
        _features = new StepView(context, "1", Translator.ImapSetup_StepFeatures);
        _signIn = new StepView(context, "2", Translator.ImapSetup_StepSignIn);
        _servers = new StepView(context, "3", Translator.ImapSetup_StepServers);
        AddView(_features);
        AddConnector(context);
        AddView(_signIn);
        AddConnector(context);
        AddView(_servers);
        SetCurrent(ImapSetupStep.SignIn);
    }

    public void SetCurrent(ImapSetupStep step)
    {
        _features.Apply(done: step != ImapSetupStep.Features, current: step == ImapSetupStep.Features);
        _signIn.Apply(done: step == ImapSetupStep.Servers, current: step == ImapSetupStep.SignIn);
        _servers.Apply(done: false, current: step == ImapSetupStep.Servers);
    }

    private void AddConnector(Context context)
    {
        var line = new View(context);
        line.SetBackgroundColor(WinoColors.Divider(context));
        var parameters = new LayoutParams(WinoUnits.Dp(context, 24), WinoUnits.Dp(context, 1)) { Gravity = GravityFlags.CenterVertical };
        AddView(line, parameters);
    }

    private sealed class StepView : LinearLayout
    {
        private readonly Context _context;
        private readonly string _number;
        private readonly TextView _circle;
        private readonly TextView _title;

        public StepView(Context context, string number, string title) : base(context)
        {
            _context = context;
            _number = number;
            Orientation = Orientation.Horizontal;
            SetGravity(GravityFlags.CenterVertical);
            _circle = new TextView(context) { Gravity = GravityFlags.Center };
            _circle.SetTextSize(ComplexUnitType.Sp, 11);
            AddView(_circle, new LayoutParams(WinoUnits.Dp(context, 22), WinoUnits.Dp(context, 22)));
            _title = new TextView(context) { Text = title };
            _title.SetTextAppearance(Resource.Style.TextAppearance_Wino_Caption);
            AddView(_title, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            {
                LeftMargin = WinoUnits.Dp(context, 6)
            });
        }

        public void Apply(bool done, bool current)
        {
            var accent = done || current;
            var fill = new GradientDrawable();
            fill.SetShape(ShapeType.Oval);
            fill.SetColor(accent ? WinoColors.Accent(_context) : WinoColors.SubtleFill(_context));
            _circle.Background = fill;
            _circle.SetTextColor(accent ? WinoColors.OnAccent(_context) : WinoColors.SecondaryText(_context));
            _circle.Typeface = done ? WinoIcons.Mono(_context) : Typeface.Default;
            _circle.Text = done ? WinoIcons.Glyph(WinoIconGlyph.Checkmark) : _number;
            _title.SetTextColor(current ? WinoColors.PrimaryText(_context) : WinoColors.SecondaryText(_context));
        }
    }
}
