using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

/// <summary>
/// Account setup progress (Windows AccountSetupProgressPage): a centred 500pt column with the
/// title, the step list with a status glyph per step, the success banner, and on failure the error
/// banner with Go Back and Try Again. The view knows nothing about the ViewModel, so the debug
/// preview can show it with sample steps.
/// </summary>
public sealed class AccountSetupProgressPage : NSView
{
    public const double ColumnWidth = 500;

    private readonly WinoInfoBar _success;
    private readonly WinoInfoBar _failure;
    private readonly NSStackView _failurePanel;

    public AccountSetupProgressPage(NSButton back, NSButton retry)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;

        var title = WinoStyle.Label(Translator.AccountSetup_Title, WinoStyle.PageTitle);
        title.Alignment = NSTextAlignment.Center;

        _success = new WinoInfoBar(WinoInfoBarSeverity.Success, null, Translator.AccountSetup_SuccessMessage) { Hidden = true };
        _failure = new WinoInfoBar(WinoInfoBarSeverity.Error, null, string.Empty);

        back.Title = Translator.AccountSetup_GoBackButton;
        retry.Title = Translator.AccountSetup_TryAgainButton;
        retry.KeyEquivalent = "\r";
        var buttons = WinoLayout.HStack(WinoStyle.Space2, back, retry);
        _failurePanel = WinoLayout.VStack(WinoStyle.Space3, _failure, buttons);
        _failurePanel.Alignment = NSLayoutAttribute.CenterX;
        _failure.WidthAnchor.ConstraintEqualTo(_failurePanel.WidthAnchor).Active = true;
        _failurePanel.Hidden = true;

        var column = WinoLayout.VStack(WinoStyle.Space6, title, Steps, _success, _failurePanel);
        column.Alignment = NSLayoutAttribute.CenterX;
        foreach (var view in column.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;

        // The column sits in the middle of the page and scrolls when a long failure outgrows it.
        var document = new FlippedView();
        document.AddSubview(column);
        var centerY = column.CenterYAnchor.ConstraintEqualTo(document.CenterYAnchor);
        centerY.Priority = 750;
        var preferredWidth = column.WidthAnchor.ConstraintEqualTo((nfloat)ColumnWidth);
        preferredWidth.Priority = 750;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.CenterXAnchor.ConstraintEqualTo(document.CenterXAnchor),
            column.WidthAnchor.ConstraintLessThanOrEqualTo(document.WidthAnchor, 1, -48),
            preferredWidth,
            column.TopAnchor.ConstraintGreaterThanOrEqualTo(document.TopAnchor, 32),
            column.BottomAnchor.ConstraintLessThanOrEqualTo(document.BottomAnchor, -32),
            centerY
        ]);
        var shrink = document.HeightAnchor.ConstraintEqualTo(0);
        shrink.Priority = 250;
        shrink.Active = true;

        var scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
            DocumentView = document
        };
        NSLayoutConstraint.ActivateConstraints(
        [
            document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
            document.HeightAnchor.ConstraintGreaterThanOrEqualTo(scroll.ContentView.HeightAnchor),
            document.TopAnchor.ConstraintEqualTo(scroll.ContentView.TopAnchor),
            document.LeadingAnchor.ConstraintEqualTo(scroll.ContentView.LeadingAnchor)
        ]);
        WinoLayout.Fill(scroll, this);
    }

    public AccountSetupStepList Steps { get; } = new();

    public bool IsSetupComplete
    {
        set => _success.Hidden = !value;
    }

    public bool IsSetupFailed
    {
        set => _failurePanel.Hidden = !value;
    }

    public string? FailureMessage
    {
        set => _failure.Message = value ?? string.Empty;
    }

    private sealed class FlippedView : NSView
    {
        public FlippedView() => TranslatesAutoresizingMaskIntoConstraints = false;
        public override bool IsFlipped => true;
    }
}

/// <summary>
/// The setup steps as rows: a 20pt status indicator in a 28pt column (empty circle while pending,
/// a spinner while running, a green check circle when done, a red dismiss circle on failure), the
/// step title, and the failure detail under a failed step. VoiceOver reads each row's automation
/// name, which carries the status in words.
/// </summary>
public sealed class AccountSetupStepList : NSView
{
    private readonly NSStackView _rows = WinoLayout.VStack(0);

    public AccountSetupStepList()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Fill(_rows, this);
        AccessibilityElement = false;
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
                    foreach (var step in steps.ToArray()) Add(new StepRow(step, dispatcher, error));
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

    private void Add(StepRow row)
    {
        _rows.AddArrangedSubview(row);
        row.WidthAnchor.ConstraintEqualTo(_rows.WidthAnchor).Active = true;
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

    private sealed class StepRow : NSView
    {
        private readonly AccountSetupStepModel _step;
        private readonly IDispatcher _dispatcher;
        private readonly Action<Exception> _error;
        private readonly WinoIconView _icon = new(WinoIconGlyph.Circle, 16) { Colorful = false };
        private readonly NSProgressIndicator _spinner = new()
        {
            Style = NSProgressIndicatorStyle.Spinning,
            ControlSize = NSControlSize.Small,
            IsDisplayedWhenStopped = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        private readonly NSTextField _title = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        private readonly NSTextField _detail = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.Critical, 0);
        private bool _detached;

        public StepRow(AccountSetupStepModel step, IDispatcher dispatcher, Action<Exception> error)
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            _step = step;
            _dispatcher = dispatcher;
            _error = error;

            var indicator = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            indicator.AddSubview(_icon);
            indicator.AddSubview(_spinner);
            WinoLayout.Size(indicator, 28, 20);
            WinoLayout.Size(_icon, 16, 16);
            NSLayoutConstraint.ActivateConstraints(
            [
                _icon.CenterXAnchor.ConstraintEqualTo(indicator.CenterXAnchor),
                _icon.CenterYAnchor.ConstraintEqualTo(indicator.CenterYAnchor),
                _spinner.CenterXAnchor.ConstraintEqualTo(indicator.CenterXAnchor),
                _spinner.CenterYAnchor.ConstraintEqualTo(indicator.CenterYAnchor),
                _spinner.WidthAnchor.ConstraintEqualTo(16),
                _spinner.HeightAnchor.ConstraintEqualTo(16)
            ]);

            var text = WinoLayout.VStack(2, _title, _detail);
            _title.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;
            _detail.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;
            AddSubview(indicator);
            AddSubview(text);
            NSLayoutConstraint.ActivateConstraints(
            [
                indicator.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
                // The indicator centres on the title line, so a wrapped error never moves it.
                indicator.CenterYAnchor.ConstraintEqualTo(_title.CenterYAnchor),
                text.LeadingAnchor.ConstraintEqualTo(indicator.TrailingAnchor, 12),
                text.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
                text.TopAnchor.ConstraintEqualTo(TopAnchor, 6),
                text.BottomAnchor.ConstraintEqualTo(BottomAnchor, -6),
                HeightAnchor.ConstraintGreaterThanOrEqualTo(32)
            ]);

            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
            _step.PropertyChanged += StepChanged;
            Apply();
        }

        public void Detach()
        {
            _detached = true;
            _step.PropertyChanged -= StepChanged;
            _spinner.StopAnimation(null);
        }

        private async void StepChanged(object? sender, PropertyChangedEventArgs args)
        {
            try { await _dispatcher.ExecuteOnUIThread(() => { if (!_detached) Apply(); }); }
            catch (Exception exception) { _error(exception); }
        }

        private void Apply()
        {
            _title.StringValue = _step.Title ?? string.Empty;
            _detail.StringValue = _step.ErrorMessage ?? string.Empty;
            _detail.Hidden = !_step.IsFailed || string.IsNullOrWhiteSpace(_step.ErrorMessage);
            _title.TextColor = _step.IsPending ? WinoStyle.SecondaryText : WinoStyle.PrimaryText;
            AccessibilityLabel = _step.AutomationName;

            _icon.Hidden = _step.IsInProgress;
            if (_step.IsInProgress) _spinner.StartAnimation(null);
            else _spinner.StopAnimation(null);
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
