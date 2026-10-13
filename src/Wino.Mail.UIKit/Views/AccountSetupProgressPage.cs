using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views;

/// <summary>
/// Account setup progress (Windows AccountSetupProgressPage, Mac AccountSetupProgressPage): the title, the step list
/// with a status glyph per step, the success banner, and on failure the error banner with Go Back and Try Again, in a
/// centred column on the theme backdrop.
/// </summary>
public sealed class AccountSetupProgressPage : UIView
{
    public const double ColumnWidth = 500;

    private readonly WinoInfoBar _success;
    private readonly WinoInfoBar _failure;
    private readonly UIStackView _failurePanel;

    public AccountSetupProgressPage(UIButton back, UIButton retry)
    {
        BackgroundColor = UIColor.SystemGroupedBackground;
        var title = WinoStyle.Label(Translator.AccountSetup_Title, WinoStyle.PageTitle, WinoStyle.PrimaryText, 0);
        title.TextAlignment = UITextAlignment.Center;
        title.AccessibilityTraits = UIAccessibilityTrait.Header;

        _success = new WinoInfoBar(WinoInfoBarSeverity.Success, null, Translator.AccountSetup_SuccessMessage) { Hidden = true };
        _failure = new WinoInfoBar(WinoInfoBarSeverity.Error, null, string.Empty);
        var buttons = WinoLayout.HStack(WinoStyle.Space3, back, retry);
        buttons.Distribution = UIStackViewDistribution.FillEqually;
        _failurePanel = WinoLayout.VStack(WinoStyle.Space3, _failure, buttons);
        _failurePanel.Alignment = UIStackViewAlignment.Fill;
        _failurePanel.Hidden = true;

        var column = WinoLayout.VStack(WinoStyle.Space6, title, Steps, _success, _failurePanel);
        column.Alignment = UIStackViewAlignment.Fill;

        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true };
        WinoLayout.Fill(scroll, this);
        // A container at least as tall as the screen keeps the column in the middle; a long failure scrolls.
        var container = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        scroll.AddSubview(container);
        container.AddSubview(column);
        var content = scroll.ContentLayoutGuide;
        var frame = scroll.FrameLayoutGuide;
        var preferredWidth = column.WidthAnchor.ConstraintEqualTo((nfloat)ColumnWidth);
        preferredWidth.Priority = 750;
        var snug = container.HeightAnchor.ConstraintEqualTo(frame.HeightAnchor);
        snug.Priority = 250;
        NSLayoutConstraint.ActivateConstraints(
        [
            container.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            container.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            container.LeadingAnchor.ConstraintEqualTo(frame.LeadingAnchor),
            container.TrailingAnchor.ConstraintEqualTo(frame.TrailingAnchor),
            container.HeightAnchor.ConstraintGreaterThanOrEqualTo(frame.HeightAnchor),
            snug,
            column.TopAnchor.ConstraintGreaterThanOrEqualTo(container.TopAnchor, 32),
            column.BottomAnchor.ConstraintLessThanOrEqualTo(container.BottomAnchor, -32),
            column.CenterYAnchor.ConstraintEqualTo(container.CenterYAnchor),
            column.CenterXAnchor.ConstraintEqualTo(container.CenterXAnchor),
            column.WidthAnchor.ConstraintLessThanOrEqualTo(container.WidthAnchor, 1, -40),
            preferredWidth
        ]);
    }

    public AccountSetupStepList Steps { get; } = new();

    public bool IsSetupComplete { set => _success.Hidden = !value; }

    public bool IsSetupFailed { set => _failurePanel.Hidden = !value; }

    public string? FailureMessage { set => _failure.Message = value ?? string.Empty; }
}

/// <summary>
/// The setup steps as rows: an empty circle while pending, a spinner while running, a green check when done and a red
/// dismiss glyph on failure, the step title and the failure detail. VoiceOver reads each row's automation name.
/// </summary>
public sealed class AccountSetupStepList : UIView
{
    private readonly UIStackView _rows = WinoLayout.VStack(0);

    public AccountSetupStepList()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _rows.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(_rows, this);
    }

    /// <summary>Mirrors the collection; changes may arrive on any thread and are applied on the UI thread.</summary>
    public IDisposable Bind(ObservableCollection<AccountSetupStepModel> steps, IDispatcher dispatcher, Action<Exception> error)
    {
        var disposed = false;
        async void Rebuild()
        {
            try
            {
                await dispatcher.ExecuteOnUIThread(() =>
                {
                    if (disposed) return;
                    Clear();
                    foreach (var step in steps.ToArray()) _rows.AddArrangedSubview(new StepRow(step, dispatcher, error));
                });
            }
            catch (Exception exception) { if (!disposed) error(exception); }
        }
        NotifyCollectionChangedEventHandler changed = (_, _) => Rebuild();
        steps.CollectionChanged += changed;
        Rebuild();
        return new ActionDisposable(() =>
        {
            disposed = true;
            steps.CollectionChanged -= changed;
            Clear();
        });
    }

    private void Clear()
    {
        foreach (var view in _rows.ArrangedSubviews)
        {
            _rows.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            (view as StepRow)?.Detach();
        }
    }

    private sealed class StepRow : UIView
    {
        private readonly AccountSetupStepModel _step;
        private readonly IDispatcher _dispatcher;
        private readonly Action<Exception> _error;
        private readonly WinoIconView _icon = new(WinoIconGlyph.Circle, 18) { Colorful = false };
        private readonly UIActivityIndicatorView _spinner = new(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true, TranslatesAutoresizingMaskIntoConstraints = false };
        private readonly UILabel _title = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        private readonly UILabel _detail = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.Critical, 0);
        private bool _detached;

        public StepRow(AccountSetupStepModel step, IDispatcher dispatcher, Action<Exception> error)
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            _step = step;
            _dispatcher = dispatcher;
            _error = error;

            var indicator = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
            _icon.TranslatesAutoresizingMaskIntoConstraints = false;
            indicator.AddSubview(_icon);
            indicator.AddSubview(_spinner);
            WinoLayout.Size(indicator, 28, 24);
            WinoLayout.Size(_icon, 18, 18);
            var text = WinoLayout.VStack(2, _title, _detail);
            text.Alignment = UIStackViewAlignment.Fill;
            AddSubview(indicator);
            AddSubview(text);
            NSLayoutConstraint.ActivateConstraints(
            [
                _icon.CenterXAnchor.ConstraintEqualTo(indicator.CenterXAnchor),
                _icon.CenterYAnchor.ConstraintEqualTo(indicator.CenterYAnchor),
                _spinner.CenterXAnchor.ConstraintEqualTo(indicator.CenterXAnchor),
                _spinner.CenterYAnchor.ConstraintEqualTo(indicator.CenterYAnchor),
                indicator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
                indicator.CenterYAnchor.ConstraintEqualTo(_title.CenterYAnchor),
                text.LeadingAnchor.ConstraintEqualTo(indicator.TrailingAnchor, 12),
                text.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
                text.TopAnchor.ConstraintEqualTo(TopAnchor, 8),
                text.BottomAnchor.ConstraintEqualTo(BottomAnchor, -8),
                HeightAnchor.ConstraintGreaterThanOrEqualTo(40)
            ]);
            IsAccessibilityElement = true;
            AccessibilityTraits = UIAccessibilityTrait.StaticText;
            _step.PropertyChanged += StepChanged;
            Apply();
        }

        public void Detach()
        {
            _detached = true;
            _step.PropertyChanged -= StepChanged;
            _spinner.StopAnimating();
        }

        private async void StepChanged(object? sender, PropertyChangedEventArgs args)
        {
            try { await _dispatcher.ExecuteOnUIThread(() => { if (!_detached) Apply(); }); }
            catch (Exception exception) { _error(exception); }
        }

        private void Apply()
        {
            _title.Text = _step.Title ?? string.Empty;
            _detail.Text = _step.ErrorMessage ?? string.Empty;
            _detail.Hidden = !_step.IsFailed || string.IsNullOrWhiteSpace(_step.ErrorMessage);
            _title.TextColor = _step.IsPending ? WinoStyle.SecondaryText : WinoStyle.PrimaryText;
            AccessibilityLabel = _step.AutomationName;

            _icon.Hidden = _step.IsInProgress;
            if (_step.IsInProgress) _spinner.StartAnimating();
            else _spinner.StopAnimating();
            switch (_step.Status)
            {
                case AccountSetupStepStatus.Succeeded:
                    _icon.Icon = WinoIconGlyph.CheckmarkCircleFilled;
                    _icon.Tint = WinoStyle.Success;
                    break;
                case AccountSetupStepStatus.Failed:
                    _icon.Icon = WinoIconGlyph.DismissCircle;
                    _icon.Tint = WinoStyle.Critical;
                    break;
                default:
                    _icon.Icon = WinoIconGlyph.Circle;
                    _icon.Tint = WinoStyle.TertiaryText;
                    break;
            }
        }
    }
}
