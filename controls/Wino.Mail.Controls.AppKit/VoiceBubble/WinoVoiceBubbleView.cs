using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Mail.Controls.Core.VoiceBubble;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.VoiceBubble;

/// <summary>
/// The AppKit voice bubble: the Wino icon with live voice bars while listening, three dots while the plan is prepared,
/// then a confirmation card, a question with a picker, or a success or failure line in the same surface.
///
/// The view only presents. The shared controller sets the text for a state, calls <see cref="Present"/>, feeds
/// <see cref="UpdateBands"/> while listening and reacts to the events. The window around it follows
/// <see cref="TargetSizeChanged"/>; the view fills that window and fades it in and out itself.
/// </summary>
public sealed partial class WinoVoiceBubbleView : NSView, IVoiceBubbleView
{
    public const double MorphSeconds = 0.34;
    private const double CornerRadius = 10;
    private const double LogoSize = 32;
    private const double PanelWidth = 300;
    private const double TranscriptWidth = 291;
    private const double TranscriptHeight = 36;
    private const double TranscriptLineHeight = 18;
    private const double DimmedOpacity = 0.65;
    private const double CountdownOpacity = 0.75;
    private const double BadgeSize = 16;

    private static readonly NSEdgeInsets CompactInsets = new(5, 5, 5, 10);
    private static readonly NSEdgeInsets CardInsets = new(8, 12, 12, 12);
    private static readonly NSEdgeInsets CollapsedInsets = new(5, 5, 5, 5);

    private readonly NSVisualEffectView _background;
    private readonly NSView _border;
    private readonly NSView _content;
    private readonly NSStackView _stack;
    private readonly NSStackView _header;
    private readonly NSButton _logo;
    private readonly NSButton _cancelBadge;
    private readonly NSView _completionBadge;
    private readonly VoiceBubbleBarsView _bars;
    private readonly VoiceBubbleDotsView _dots;
    private readonly NSStackView _inlinePanel;
    private readonly NSTextField _inlineText;
    private readonly NSButton _inlineConfirm;
    private readonly NSButton _inlineDecline;
    private readonly NSStackView _messagePanel;
    private readonly NSImageView _messageGlyph;
    private readonly NSTextField _messageText;
    private readonly NSButton _retry;
    private readonly NSButton _dismiss;
    private readonly NSView _transcriptViewport;
    private readonly NSTextField _transcriptText;
    private readonly CALayer _countdown;
    private NSLayoutConstraint? _contentWidth;
    private NSLayoutConstraint? _contentHeight;
    private NSTrackingArea? _trackingArea;
    private NSTimer? _countdownTimer;
    private NSTimer? _exitTimer;
    private DateTime _countdownStartedAt;
    private double _countdownRemaining;
    private VoiceBubbleState _presentedState = VoiceBubbleState.Hidden;
    private int _version;
    private bool _isShown;
    private bool _isExiting;
    private bool _isPointerOver;
    private bool _transcriptVisible;
    private bool _collapsed;

    public WinoVoiceBubbleView() : base(new CGRect(0, 0, 44, 44))
    {
        WantsLayer = true;
        AutoresizesSubviews = true;

        _background = new NSVisualEffectView(Bounds)
        {
            Material = NSVisualEffectMaterial.Popover,
            BlendingMode = NSVisualEffectBlendingMode.BehindWindow,
            State = NSVisualEffectState.Active,
            AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable,
            MaskImage = CreateMaskImage(CornerRadius)
        };
        AddSubview(_background);

        _border = new NSView(Bounds) { WantsLayer = true, AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable };
        _border.Layer!.CornerRadius = (nfloat)CornerRadius;
        _border.Layer.BorderWidth = 1;
        AddSubview(_border);

        _content = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        AddSubview(_content);
        NSLayoutConstraint.ActivateConstraints(
        [
            _content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 1),
            _content.TopAnchor.ConstraintEqualTo(TopAnchor, 1)
        ]);

        _logo = new NSButton
        {
            Bordered = false,
            Image = CreateLogoImage(),
            ImageScaling = NSImageScale.ProportionallyUpOrDown,
            Title = string.Empty,
            ToolTip = Translator.Buttons_Send,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        if (_logo.Cell is NSButtonCell logoCell)
            logoCell.ImageDimsWhenDisabled = false;
        _logo.Activated += (_, _) =>
        {
            if (_presentedState == VoiceBubbleState.Listening)
                SendRequested?.Invoke(this, EventArgs.Empty);
        };
        WinoAccessibility.Label(_logo, Translator.Buttons_Send);
        WinoLayout.Size(_logo, LogoSize, LogoSize);

        _bars = new VoiceBubbleBarsView();
        WinoAccessibility.Label(_bars, Translator.VoiceBubble_Listening);
        _dots = new VoiceBubbleDotsView();
        WinoAccessibility.Label(_dots, Translator.VoiceBubble_Analyzing);

        _inlineText = CreateWrappingLabel(WinoStyle.Body, PanelWidth);
        _inlineConfirm = CreateButton(isDefault: true, () => ConfirmRequested?.Invoke(this, EventArgs.Empty));
        _inlineDecline = CreateButton(isDefault: false, () => CancelRequested?.Invoke(this, EventArgs.Empty));
        _inlinePanel = WinoLayout.HStack(8, CreateGlyph(GlyphKind.Question), _inlineText, _inlineConfirm, _inlineDecline);

        _messageGlyph = CreateGlyph(GlyphKind.Success);
        _messageText = CreateWrappingLabel(WinoStyle.Body, PanelWidth);
        _retry = CreateButton(isDefault: false, () => RetryRequested?.Invoke(this, EventArgs.Empty));
        _dismiss = new NSButton
        {
            Bordered = false,
            Image = WinoStyle.Symbol("xmark", Translator.Buttons_Dismiss, 10, NSFontWeight.Semibold),
            ContentTintColor = NSColor.SecondaryLabel,
            ToolTip = Translator.Buttons_Dismiss,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _dismiss.Activated += (_, _) => DismissSelf();
        WinoAccessibility.Label(_dismiss, Translator.Buttons_Dismiss);
        WinoLayout.Size(_dismiss, 22, 22);
        _messagePanel = WinoLayout.HStack(8, _messageGlyph, _messageText, _retry, _dismiss);

        BuildStepsPanel();
        BuildQuestionPanel();

        _header = WinoLayout.HStack(8, _logo, _bars, _dots, _inlinePanel, _messagePanel, _stepsPanel, _questionPanel);
        _header.Alignment = NSLayoutAttribute.CenterY;

        _transcriptText = new NSTextField
        {
            Editable = false,
            Selectable = false,
            Bordered = false,
            DrawsBackground = false,
            Font = WinoStyle.Body,
            MaximumNumberOfLines = 0,
            LineBreakMode = NSLineBreakMode.ByWordWrapping,
            PreferredMaxLayoutWidth = (nfloat)TranscriptWidth
        };
        _transcriptText.Cell.Wraps = true;
        _transcriptViewport = new FlippedView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        _transcriptViewport.Layer!.MasksToBounds = true;
        _transcriptViewport.AddSubview(_transcriptText);
        WinoLayout.Size(_transcriptViewport, TranscriptWidth, TranscriptHeight);
        var transcriptRow = WinoLayout.HStack(0, new NSView { TranslatesAutoresizingMaskIntoConstraints = false }, _transcriptViewport);
        WinoLayout.Size(transcriptRow.ArrangedSubviews[0], 4, 1);

        _stack = WinoLayout.VStack(6, _header, transcriptRow);
        _stack.Alignment = NSLayoutAttribute.Leading;
        _stack.EdgeInsets = CompactInsets;
        _stack.DetachesHiddenViews = true;
        _content.AddSubview(_stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            _stack.LeadingAnchor.ConstraintEqualTo(_content.LeadingAnchor),
            _stack.TopAnchor.ConstraintEqualTo(_content.TopAnchor)
        ]);

        _cancelBadge = new NSButton
        {
            Bordered = false,
            WantsLayer = true,
            Image = WinoStyle.Symbol("xmark", Translator.Buttons_Cancel, 7, NSFontWeight.Bold),
            ContentTintColor = NSColor.Label,
            ToolTip = Translator.Buttons_Cancel,
            AlphaValue = 0,
            Frame = new CGRect(1 + 5 + LogoSize - 12, 1, BadgeSize, BadgeSize)
        };
        _cancelBadge.Layer!.CornerRadius = (nfloat)(BadgeSize / 2);
        _cancelBadge.Layer.BorderWidth = 1;
        _cancelBadge.Activated += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        WinoAccessibility.Label(_cancelBadge, Translator.Buttons_Cancel);
        AddSubview(_cancelBadge);

        _completionBadge = new NSView(new CGRect(1 + 5 + LogoSize - 12, 1 + 5 + LogoSize - 12, BadgeSize, BadgeSize)) { WantsLayer = true, AlphaValue = 0 };
        _completionBadge.Layer!.CornerRadius = (nfloat)(BadgeSize / 2);
        var completionGlyph = new NSImageView(_completionBadge.Bounds)
        {
            Image = WinoStyle.Symbol("checkmark.circle.fill", null, 14),
            ContentTintColor = WinoStyle.Success
        };
        _completionBadge.AddSubview(completionGlyph);
        AddSubview(_completionBadge);

        _countdown = new CALayer { AnchorPoint = new CGPoint(0, 0.5), Opacity = 0 };
        Layer!.AddSublayer(_countdown);

        ApplyColors();
        ResetToHidden();
    }

    public override bool IsFlipped => true;

    public override bool AcceptsFirstResponder() => true;

    /// <summary>The size the window should take to fit the bubble, and whether the change should animate.</summary>
    public event EventHandler<VoiceBubbleSizeChange>? TargetSizeChanged;

    public event EventHandler? SendRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler? ConfirmRequested;

    public event EventHandler? RetryRequested;

    public event EventHandler? Dismissed;

    public event EventHandler? ExitCompleted;

    public event EventHandler<string>? QuestionFilterChanged;

    public event EventHandler<VoiceBubbleChoice>? ChoiceSelected;

    public VoiceBubbleState State { get; private set; } = VoiceBubbleState.Hidden;

    public CGSize TargetSize { get; private set; } = new(44, 44);

    public int SuccessDismissMilliseconds { get; set; } = 3000;

    public int FailureDismissMilliseconds { get; set; } = 6000;

    public bool IsCountdownVisible { get; set; } = true;

    public bool CanRetry { get; set; } = true;

    public bool ShowsTranscript { get; set; }

    public string ListeningText { get; set; } = string.Empty;

    public string ConfirmText { get; set; } = string.Empty;

    public string DeclineText { get; set; } = string.Empty;

    public string RetryText { get; set; } = string.Empty;

    public string ConfirmSelectedText { get; set; } = string.Empty;

    public string NoticeTitle { get; set; } = string.Empty;

    public string QuestionSearchPlaceholder { get; set; } = string.Empty;

    public string QuestionEmptyText { get; set; } = string.Empty;

    public string QuestionCancelText { get; set; } = string.Empty;

    public string? Transcript
    {
        get => _transcript;
        set
        {
            _transcript = value;
            RefreshTranscript();
        }
    }

    private string? _transcript;

    public string? InterimTranscript
    {
        get => _interimTranscript;
        set
        {
            _interimTranscript = value;
            RefreshTranscript();
        }
    }

    private string? _interimTranscript;

    public string? QuestionText { get; set; }

    public string? QuestionCaption { get; set; }

    public bool IsQuestionSearchable { get; set; } = true;

    public VoiceBubbleText? Message { get; set; }

    public VoiceBubbleText? ConfirmationText { get; set; }

    public IReadOnlyList<VoiceBubbleStep>? ConfirmationSteps { get; set; }

    public IReadOnlyList<VoiceBubbleStepGroup>? ConfirmationGroups { get; set; }

    private static bool AnimationsEnabled => !NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldReduceMotion;

    /// <summary>Moves to <paramref name="state"/>, or re-reads the text of the current state when it is the same.</summary>
    public void Present(VoiceBubbleState state)
    {
        State = state;
        PresentCore(state);
    }

    public void UpdateBands(ReadOnlySpan<float> bands)
    {
        if (_presentedState == VoiceBubbleState.Listening)
            _bars.SetBands(bands);
    }

    private void PresentCore(VoiceBubbleState state)
    {
        _version++;
        StopCountdown();
        _exitTimer?.Invalidate();
        _exitTimer = null;

        if (state == VoiceBubbleState.Hidden)
        {
            var previous = _presentedState;
            _presentedState = VoiceBubbleState.Hidden;
            _dots.Stop();
            if (_isShown && !_isExiting)
                PlayExit(previous == VoiceBubbleState.Success);
            UpdateBadges();
            return;
        }

        var wasShown = _isShown && !_isExiting;
        _presentedState = state;
        _collapsed = false;
        ApplyContent(state);
        Resize(animate: wasShown);
        PlayEnter(wasShown);
        _isShown = true;
        _isExiting = false;
        UpdateBadges();
        StartCountdown(state);
        Window?.MakeFirstResponder(FocusTarget(state));
    }

    private void ApplyContent(VoiceBubbleState state)
    {
        var isCard = state == VoiceBubbleState.Question || (state == VoiceBubbleState.Confirmation && HasSteps);

        _logo.Hidden = isCard;
        _logo.Enabled = state == VoiceBubbleState.Listening;
        _bars.Hidden = state != VoiceBubbleState.Listening;
        _dots.Hidden = state != VoiceBubbleState.Analyzing;
        _inlinePanel.Hidden = state != VoiceBubbleState.Confirmation || HasSteps;
        _stepsPanel.Hidden = state != VoiceBubbleState.Confirmation || !HasSteps;
        _messagePanel.Hidden = state is not (VoiceBubbleState.Success or VoiceBubbleState.Failed);
        _questionPanel.Hidden = state != VoiceBubbleState.Question;
        _stack.EdgeInsets = isCard ? CardInsets : CompactInsets;
        _header.Alignment = isCard ? NSLayoutAttribute.Top : NSLayoutAttribute.CenterY;

        if (state == VoiceBubbleState.Listening)
            _bars.Reset();

        if (state == VoiceBubbleState.Analyzing && AnimationsEnabled)
            _dots.Start();
        else
            _dots.Stop();

        switch (state)
        {
            case VoiceBubbleState.Confirmation when HasSteps:
                BuildSteps();
                break;
            case VoiceBubbleState.Confirmation:
                _inlineText.AttributedStringValue = ToAttributed(ConfirmationText, WinoStyle.Body, WinoStyle.BodyStrong);
                _inlineConfirm.Title = ConfirmText;
                _inlineDecline.Title = DeclineText;
                break;
            case VoiceBubbleState.Success or VoiceBubbleState.Failed:
                ApplyGlyph(_messageGlyph, state == VoiceBubbleState.Success ? GlyphKind.Success : GlyphKind.Error);
                _messageText.AttributedStringValue = ToAttributed(Message, WinoStyle.Body, WinoStyle.BodyStrong);
                _retry.Title = RetryText;
                _retry.Hidden = state != VoiceBubbleState.Failed || !CanRetry;
                _dismiss.Hidden = state != VoiceBubbleState.Failed;
                break;
            case VoiceBubbleState.Question:
                ApplyQuestion();
                break;
        }

        _transcriptVisible = IsTranscriptVisible(state);
        _bars.SetWide(_transcriptVisible);
        UpdateTranscriptText(animate: false);
    }

    private bool HasSteps => ConfirmationGroups is { Count: > 0 } || ConfirmationSteps is { Count: > 0 };

    private NSResponder FocusTarget(VoiceBubbleState state) => state switch
    {
        VoiceBubbleState.Question when IsQuestionSearchable => _questionSearch,
        VoiceBubbleState.Question => _questionTable,
        _ => this
    };

    /// <summary>
    /// Measures the content at its natural size, pins it there so text does not rewrap while the window morphs, and
    /// asks the window for the new size.
    /// </summary>
    private void Resize(bool animate)
    {
        _contentWidth?.Active = false;
        _contentHeight?.Active = false;
        _stack.LayoutSubtreeIfNeeded();
        var fitting = _stack.FittingSize;

        _contentWidth = _content.WidthAnchor.ConstraintEqualTo(fitting.Width);
        _contentHeight = _content.HeightAnchor.ConstraintEqualTo(fitting.Height);
        _contentWidth.Active = true;
        _contentHeight.Active = true;

        TargetSize = new CGSize(Math.Ceiling(fitting.Width) + 2, Math.Ceiling(fitting.Height) + 2);
        TargetSizeChanged?.Invoke(this, new VoiceBubbleSizeChange(TargetSize, animate && AnimationsEnabled));
    }

    private void PlayEnter(bool wasShown)
    {
        if (Window is not { } window)
            return;

        if (wasShown && window.AlphaValue >= 1)
            return;

        if (!AnimationsEnabled)
        {
            window.AlphaValue = 1;
            return;
        }

        NSAnimationContext.RunAnimation(context =>
        {
            context.Duration = 0.16;
            context.TimingFunction = CAMediaTimingFunction.FromControlPoints(0.1f, 0.9f, 0.2f, 1f);
            WinoWindowAnimator.SetAlphaValue(window, 1);
        });
    }

    /// <summary>
    /// A finished command shrinks to the Wino icon with a check before it fades; every other state just fades.
    /// </summary>
    private void PlayExit(bool collapse)
    {
        _isExiting = true;
        var version = _version;

        if (Window is not { } window || !AnimationsEnabled)
        {
            FinishExit(version);
            return;
        }

        if (!collapse)
        {
            FadeOut(window, 0.18, version);
            return;
        }

        _collapsed = true;
        _logo.Hidden = false;
        _logo.Enabled = false;
        foreach (var view in new NSView[] { _bars, _dots, _inlinePanel, _stepsPanel, _messagePanel, _questionPanel })
            view.Hidden = true;
        _transcriptVisible = false;
        _transcriptViewport.Superview!.Hidden = true;
        _stack.EdgeInsets = CollapsedInsets;
        _completionBadge.AlphaValue = 1;
        Resize(animate: true);

        _exitTimer = NSTimer.CreateScheduledTimer(0.52, false, _ =>
        {
            if (version == _version)
                FadeOut(window, 0.26, version);
        });
    }

    private void FadeOut(NSWindow window, double seconds, int version)
    {
        NSAnimationContext.RunAnimation(context =>
        {
            context.Duration = seconds;
            context.TimingFunction = CAMediaTimingFunction.FromControlPoints(0.7f, 0f, 1f, 0.5f);
            WinoWindowAnimator.SetAlphaValue(window, 0);
        }, () => FinishExit(version));
    }

    private void FinishExit(int version)
    {
        if (version != _version)
            return;

        ResetToHidden();
        ExitCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ResetToHidden()
    {
        _isShown = false;
        _isExiting = false;
        _collapsed = false;
        _completionBadge.AlphaValue = 0;
        _dots.Stop();
        if (Window is { } window)
            window.AlphaValue = 0;
    }

    private void DismissSelf()
    {
        if (State == VoiceBubbleState.Hidden)
            return;

        Present(VoiceBubbleState.Hidden);
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateBadges()
    {
        var showCancel = _isPointerOver && !_collapsed && _presentedState is VoiceBubbleState.Listening or VoiceBubbleState.Analyzing;
        _cancelBadge.AlphaValue = showCancel ? 1 : 0;
        _cancelBadge.Enabled = showCancel;
        if (!_collapsed)
            _completionBadge.AlphaValue = 0;
    }

    public override void KeyDown(NSEvent theEvent)
    {
        switch (theEvent.KeyCode)
        {
            case 53:
                HandleEscape();
                return;
            case 36 or 76 when _presentedState == VoiceBubbleState.Listening:
                SendRequested?.Invoke(this, EventArgs.Empty);
                return;
        }

        base.KeyDown(theEvent);
    }

    [Export("cancelOperation:")]
    public void CancelOperation(NSObject? sender) => HandleEscape();

    private void HandleEscape()
    {
        switch (_presentedState)
        {
            case VoiceBubbleState.Listening or VoiceBubbleState.Analyzing or VoiceBubbleState.Confirmation or VoiceBubbleState.Question:
                CancelRequested?.Invoke(this, EventArgs.Empty);
                break;
            case VoiceBubbleState.Success or VoiceBubbleState.Failed:
                DismissSelf();
                break;
        }
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_trackingArea is not null)
            RemoveTrackingArea(_trackingArea);

        _trackingArea = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect, this, null);
        AddTrackingArea(_trackingArea);
    }

    public override void MouseEntered(NSEvent theEvent)
    {
        _isPointerOver = true;
        UpdateBadges();
        PauseCountdown();
    }

    public override void MouseExited(NSEvent theEvent)
    {
        _isPointerOver = false;
        UpdateBadges();
        ResumeCountdown();
    }

    public override void Layout()
    {
        base.Layout();
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        _countdown.Bounds = new CGRect(0, 0, Bounds.Width, 2);
        _countdown.Position = new CGPoint(0, Bounds.Height - 1);
        CATransaction.Commit();
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        ApplyColors();
    }

    private void ApplyColors()
    {
        EffectiveAppearance.PerformAsCurrentDrawingAppearance(() =>
        {
            _border.Layer!.BorderColor = NSColor.Separator.CGColor;
            _cancelBadge.Layer!.BackgroundColor = NSColor.ControlBackground.CGColor;
            _cancelBadge.Layer.BorderColor = NSColor.Separator.CGColor;
            _completionBadge.Layer!.BackgroundColor = NSColor.WindowBackground.CGColor;
            _countdown.BackgroundColor = VoiceBubbleBarsView.BarColor.CGColor;
        });
        _bars.ApplyColors();
        _dots.ApplyColors();
        UpdateTranscriptText(animate: false);
    }

    private void StartCountdown(VoiceBubbleState state)
    {
        if (state is not (VoiceBubbleState.Success or VoiceBubbleState.Failed))
        {
            _countdown.Opacity = 0;
            return;
        }

        var delay = (state == VoiceBubbleState.Success ? SuccessDismissMilliseconds : FailureDismissMilliseconds) / 1000.0;
        _countdown.Opacity = IsCountdownVisible ? (float)CountdownOpacity : 0;
        _countdown.Transform = CATransform3D.Identity;
        if (delay <= 0)
            return;

        _countdownRemaining = delay;
        var animation = CABasicAnimation.FromKeyPath("transform.scale.x");
        animation.From = NSNumber.FromDouble(1);
        animation.To = NSNumber.FromDouble(0);
        animation.Duration = delay;
        animation.FillMode = CAFillMode.Forwards;
        animation.RemovedOnCompletion = false;
        _countdown.Speed = 1;
        _countdown.TimeOffset = 0;
        _countdown.BeginTime = 0;
        _countdown.AddAnimation(animation, "countdown");

        if (_isPointerOver)
            PauseCountdown();
        else
            ScheduleCountdown();
    }

    private void ScheduleCountdown()
    {
        _countdownTimer?.Invalidate();
        _countdownStartedAt = DateTime.UtcNow;
        var version = _version;
        _countdownTimer = NSTimer.CreateScheduledTimer(_countdownRemaining, false, _ =>
        {
            if (version == _version)
                DismissSelf();
        });
    }

    private void PauseCountdown()
    {
        if (_countdownTimer is null || _countdown.AnimationForKey("countdown") is null)
            return;

        _countdownTimer.Invalidate();
        _countdownTimer = null;
        _countdownRemaining = Math.Max(0, _countdownRemaining - (DateTime.UtcNow - _countdownStartedAt).TotalSeconds);
        var paused = _countdown.ConvertTimeFromLayer(CAAnimation.CurrentMediaTime(), null);
        _countdown.Speed = 0;
        _countdown.TimeOffset = paused;
    }

    private void ResumeCountdown()
    {
        if (_countdownTimer is not null || _countdown.AnimationForKey("countdown") is null || _presentedState is not (VoiceBubbleState.Success or VoiceBubbleState.Failed))
            return;

        var paused = _countdown.TimeOffset;
        _countdown.Speed = 1;
        _countdown.TimeOffset = 0;
        _countdown.BeginTime = 0;
        _countdown.BeginTime = _countdown.ConvertTimeFromLayer(CAAnimation.CurrentMediaTime(), null) - paused;
        ScheduleCountdown();
    }

    private void StopCountdown()
    {
        _countdownTimer?.Invalidate();
        _countdownTimer = null;
        _countdown.RemoveAllAnimations();
        _countdown.Speed = 1;
        _countdown.TimeOffset = 0;
        _countdown.Opacity = 0;
    }

    private bool IsTranscriptVisible(VoiceBubbleState state)
        => ShowsTranscript && (state == VoiceBubbleState.Listening || (state == VoiceBubbleState.Analyzing && HasWords));

    private bool HasWords => !string.IsNullOrWhiteSpace(Transcript) || !string.IsNullOrWhiteSpace(InterimTranscript);

    /// <summary>New words only scroll the transcript; the bubble resizes only when the transcript row appears or goes.</summary>
    private void RefreshTranscript()
    {
        if (!_isShown || _collapsed || _presentedState is not (VoiceBubbleState.Listening or VoiceBubbleState.Analyzing))
            return;

        var visible = IsTranscriptVisible(_presentedState);
        if (visible != _transcriptVisible)
        {
            _transcriptVisible = visible;
            _bars.SetWide(visible);
            UpdateTranscriptText(animate: false);
            Resize(animate: true);
            return;
        }

        UpdateTranscriptText(animate: true);
    }

    private void UpdateTranscriptText(bool animate)
    {
        _transcriptViewport.Superview!.Hidden = !_transcriptVisible;
        if (!_transcriptVisible)
        {
            _transcriptText.SetFrameOrigin(CGPoint.Empty);
            return;
        }

        var paragraph = new NSMutableParagraphStyle
        {
            MinimumLineHeight = (nfloat)TranscriptLineHeight,
            MaximumLineHeight = (nfloat)TranscriptLineHeight,
            LineBreakMode = NSLineBreakMode.ByWordWrapping
        };
        var text = new NSMutableAttributedString();
        var heard = Transcript?.Trim() ?? string.Empty;
        var pending = InterimTranscript?.Trim() ?? string.Empty;
        if (heard.Length == 0 && pending.Length == 0)
        {
            text.Append(new NSAttributedString(ListeningText, new NSStringAttributes { Font = WinoStyle.Body, ForegroundColor = NSColor.SecondaryLabel, ParagraphStyle = paragraph }));
        }
        else
        {
            text.Append(new NSAttributedString(heard, new NSStringAttributes { Font = WinoStyle.Body, ForegroundColor = NSColor.Label, ParagraphStyle = paragraph }));
            if (pending.Length > 0)
                text.Append(new NSAttributedString((heard.Length > 0 ? " " : string.Empty) + pending, new NSStringAttributes { Font = WinoStyle.Body, ForegroundColor = NSColor.TertiaryLabel, ParagraphStyle = paragraph }));
        }

        _transcriptText.AttributedStringValue = text;
        _transcriptText.AlphaValue = _presentedState == VoiceBubbleState.Analyzing ? (nfloat)DimmedOpacity : 1;
        var height = Math.Ceiling(_transcriptText.Cell.CellSizeForBounds(new CGRect(0, 0, TranscriptWidth, 10000)).Height);
        _transcriptText.SetFrameSize(new CGSize(TranscriptWidth, height));

        var target = new CGPoint(0, -Math.Max(0, height - TranscriptHeight));
        if (Math.Abs(target.Y - _transcriptText.Frame.Y) < 0.5)
            return;

        if (animate && AnimationsEnabled)
        {
            NSAnimationContext.RunAnimation(context =>
            {
                context.Duration = 0.28;
                context.TimingFunction = CAMediaTimingFunction.FromControlPoints(0.1f, 0.9f, 0.2f, 1f);
                ((NSView)_transcriptText.Animator).SetFrameOrigin(target);
            });
        }
        else
        {
            _transcriptText.SetFrameOrigin(target);
        }
    }

    private static NSImage CreateLogoImage()
        => NSImage.ImageNamed("AppIcon") ?? NSApplication.SharedApplication.ApplicationIconImage;

    /// <summary>A stretchable rounded-rect mask, so the material and the window shadow follow the rounded shape.</summary>
    private static NSImage CreateMaskImage(double radius)
    {
        var edge = (radius * 2) + 1;
        var image = NSImage.ImageWithSize(new CGSize(edge, edge), false, rect =>
        {
            NSColor.Black.SetFill();
            NSBezierPath.FromRoundedRect(rect, (nfloat)radius, (nfloat)radius).Fill();
            return true;
        });
        image.CapInsets = new NSEdgeInsets((nfloat)radius, (nfloat)radius, (nfloat)radius, (nfloat)radius);
        image.ResizingMode = NSImageResizingMode.Stretch;
        return image;
    }

    private sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}

/// <summary>The window size a <see cref="WinoVoiceBubbleView"/> needs, and whether the window should morph to it.</summary>
public readonly record struct VoiceBubbleSizeChange(CGSize Size, bool Animate);
