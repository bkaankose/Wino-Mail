using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Wino.Mail.Controls.SynchronizationButton;

/// <summary>
/// Title bar button that starts a synchronization and reports the one that is running.
/// Idle it is a plain icon button carrying the Sync glyph; while synchronizing it swaps the
/// glyph for a <see cref="ProgressRing"/> and grows to the right to show
/// <see cref="Description"/>.
///
/// The growth is animated with the Composition API rather than XAML storyboards: the pill
/// background is wiped open by an animated inset clip while the description fades in through
/// an implicit show animation and slides out from behind the icon.
///
/// The control is domain agnostic: it never names mail, calendars, contacts or tasks, and
/// never composes its own wording. Whatever hosts it supplies the text, the command and the
/// state.
/// </summary>
public sealed partial class WinoSynchronizationButton : Button
{
    private const string SynchronizingStateName = "Synchronizing";
    private const string IdleStateName = "Idle";

    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DescriptionShowDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan DescriptionHideDuration = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan PillFadeInDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan PillFadeOutDuration = TimeSpan.FromMilliseconds(110);

    private readonly UISettings _uiSettings = new();

    private Border? _pillBackground;
    private TextBlock? _descriptionText;
    private FrameworkElement? _overlayContent;
    private ToolTip? _toolTip;
    private bool _isRevealPending;

    /// <summary>
    /// The collapsed footprint: the icon slot, and the whole hit target while idle.
    /// </summary>
    private const float IconSlotSize = 36f;

    /// <summary>
    /// Whether a synchronization is running. Swaps the glyph for the ring and grows the
    /// button to show <see cref="Description"/>.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsSynchronizing { get; set; }

    /// <summary>
    /// Forwarded to the progress ring. True when the host cannot say how much work is left.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsIndeterminate { get; set; }

    /// <summary>
    /// Completion percentage (0-100), forwarded to the progress ring. Ignored while
    /// <see cref="IsIndeterminate"/> is set.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = 0d)]
    public partial double Progress { get; set; }

    /// <summary>
    /// Text shown beside the ring while synchronizing, e.g. "Syncing Inbox".
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Description { get; set; }

    /// <summary>
    /// Tooltip shown while collapsed, e.g. "Sync calendars". While synchronizing the tooltip
    /// follows <see cref="Description"/> instead, because the collapsed wording no longer
    /// describes what the button is doing.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string IdleToolTip { get; set; }

    public WinoSynchronizationButton()
    {
        DefaultStyleKey = typeof(WinoSynchronizationButton);

        RegisterPropertyChangedCallback(IsSynchronizingProperty, OnSynchronizationPropertyChanged);
        RegisterPropertyChangedCallback(DescriptionProperty, OnSynchronizationPropertyChanged);
        RegisterPropertyChangedCallback(IdleToolTipProperty, OnSynchronizationPropertyChanged);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_overlayContent is not null)
        {
            _overlayContent.SizeChanged -= OnOverlayContentSizeChanged;
        }

        _pillBackground = GetTemplateChild("PART_PillBackground") as Border;
        _descriptionText = GetTemplateChild("PART_DescriptionText") as TextBlock;
        _overlayContent = GetTemplateChild("PART_OverlayContent") as FrameworkElement;

        if (_overlayContent is not null)
        {
            // The control itself is a fixed 36x36; only the overhanging overlay grows, so it
            // is the one whose size change marks the end of the expansion.
            _overlayContent.SizeChanged += OnOverlayContentSizeChanged;
        }

        ConfigureImplicitAnimations();

        // The first pass is not a transition; the button simply arrives in whatever state
        // the host already set.
        UpdateSynchronizationState(useTransitions: false);
        UpdateToolTip();
    }

    private void OnSynchronizationPropertyChanged(DependencyObject sender, DependencyProperty property)
    {
        if (property == IsSynchronizingProperty)
        {
            UpdateSynchronizationState(useTransitions: true);
        }

        UpdateToolTip();
    }

    private void UpdateSynchronizationState(bool useTransitions)
    {
        // The reveal needs the width the button is growing to, which only exists after the
        // state change has been laid out. SizeChanged is where that becomes known.
        _isRevealPending = useTransitions && IsSynchronizing && AreAnimationsEnabled;

        VisualStateManager.GoToState(this, IsSynchronizing ? SynchronizingStateName : IdleStateName, useTransitions);
    }

    private bool AreAnimationsEnabled => _uiSettings.AnimationsEnabled;

    #region Composition animations

    /// <summary>
    /// Implicit show/hide animations run against the element's own visibility, so the
    /// description can animate out before the visual state actually collapses it.
    /// </summary>
    private void ConfigureImplicitAnimations()
    {
        if (_pillBackground is not null)
        {
            var pillVisual = ElementCompositionPreview.GetElementVisual(_pillBackground);
            var compositor = pillVisual.Compositor;

            ElementCompositionPreview.SetImplicitShowAnimation(
                _pillBackground,
                CreateOpacityAnimation(compositor, 1f, PillFadeInDuration));

            ElementCompositionPreview.SetImplicitHideAnimation(
                _pillBackground,
                CreateOpacityAnimation(compositor, 0f, PillFadeOutDuration));
        }

        if (_descriptionText is null)
            return;

        ElementCompositionPreview.SetIsTranslationEnabled(_descriptionText, true);

        var textCompositor = ElementCompositionPreview.GetElementVisual(_descriptionText).Compositor;

        // The slide is driven explicitly once the reveal width is known, so the implicit
        // show animation only has to fade the text in behind it.
        ElementCompositionPreview.SetImplicitShowAnimation(
            _descriptionText,
            CreateOpacityAnimation(textCompositor, 1f, DescriptionShowDuration, TimeSpan.FromMilliseconds(60)));

        var hideGroup = textCompositor.CreateAnimationGroup();
        hideGroup.Add(CreateOpacityAnimation(textCompositor, 0f, DescriptionHideDuration));
        hideGroup.Add(CreateTranslationAnimation(textCompositor, null, -10f, DescriptionHideDuration));
        ElementCompositionPreview.SetImplicitHideAnimation(_descriptionText, hideGroup);
    }

    private static ScalarKeyFrameAnimation CreateOpacityAnimation(
        Compositor compositor,
        float to,
        TimeSpan duration,
        TimeSpan? delay = null)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, to);
        animation.Duration = duration;
        animation.Target = "Opacity";

        if (delay.HasValue)
        {
            animation.DelayTime = delay.Value;
        }

        return animation;
    }

    private static ScalarKeyFrameAnimation CreateTranslationAnimation(
        Compositor compositor,
        float? from,
        float to,
        TimeSpan duration)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();

        if (from.HasValue)
        {
            animation.InsertKeyFrame(0f, from.Value);
        }

        animation.InsertKeyFrame(1f, to, CreateStandardEasing(compositor));
        animation.Duration = duration;
        animation.Target = "Translation.X";

        return animation;
    }

    private static CompositionEasingFunction CreateStandardEasing(Compositor compositor)
        => compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

    private void OnOverlayContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // ActualWidth does not notify, so the pill cannot track the content through a
        // binding. It is stretched here instead, every time the content resizes.
        if (_pillBackground is not null)
        {
            _pillBackground.Width = e.NewSize.Width;
        }

        if (!_isRevealPending)
            return;

        if (!IsSynchronizing)
        {
            _isRevealPending = false;
            return;
        }

        // The collapsed overlay is exactly the icon slot. Taking it from the constant
        // rather than the previous size keeps the reveal correct even when the state change
        // and the first layout pass land together.
        var fromWidth = IconSlotSize;
        var toWidth = (float)e.NewSize.Width;

        // The description does not always measure in the same pass that made it visible,
        // so an intermediate pass can still report the collapsed width. Stay pending until
        // the button has actually grown, otherwise the reveal is spent on a no-op.
        if (toWidth <= fromWidth || fromWidth <= 0f)
            return;

        _isRevealPending = false;

        PlayPillReveal(fromWidth, toWidth);
        PlayDescriptionSlide(toWidth - fromWidth);
    }

    /// <summary>
    /// Wipes the pill background open from the collapsed width to the expanded one, so the
    /// pill reads as extending to the right rather than appearing at full width.
    ///
    /// This uses an <see cref="InsetClip"/> rather than a geometric clip: XAML manages the
    /// element's own visual, and only inset clips are honoured on it. The travelling right
    /// edge is square, which the pill's simultaneous fade-in hides.
    /// </summary>
    private void PlayPillReveal(float fromWidth, float toWidth)
    {
        if (_pillBackground is null)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(_pillBackground);
        var compositor = visual.Compositor;

        var clip = compositor.CreateInsetClip();
        visual.Clip = clip;

        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, toWidth - fromWidth);
        animation.InsertKeyFrame(1f, 0f, CreateStandardEasing(compositor));
        animation.Duration = RevealDuration;

        // The clip is only a reveal device. Leaving it attached would crop the pill the
        // next time the button is measured wider, so it goes away with the animation.
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) => visual.Clip = null;

        clip.StartAnimation("RightInset", animation);

        batch.End();
    }

    /// <summary>
    /// Slides the description out of the pill as it opens. The implicit show animation
    /// already fades it in; this keeps its motion tied to the reveal's own timing.
    /// </summary>
    private void PlayDescriptionSlide(float distance)
    {
        if (_descriptionText is null)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(_descriptionText);
        var compositor = visual.Compositor;

        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, -distance);
        animation.InsertKeyFrame(1f, 0f, CreateStandardEasing(compositor));
        animation.Duration = RevealDuration;
        animation.Target = "Translation.X";

        visual.StartAnimation("Translation.X", animation);
    }

    #endregion

    /// <summary>
    /// The tooltip is owned by the control rather than the host so the two texts cannot
    /// fall out of step with the state that decides which one applies.
    /// </summary>
    private void UpdateToolTip()
    {
        var text = IsSynchronizing
            ? (string.IsNullOrEmpty(Description) ? IdleToolTip : Description)
            : IdleToolTip;

        if (string.IsNullOrEmpty(text))
        {
            if (_toolTip is not null)
            {
                ToolTipService.SetToolTip(this, null);
                _toolTip = null;
            }

            return;
        }

        if (_toolTip is null)
        {
            _toolTip = new ToolTip();
            ToolTipService.SetToolTip(this, _toolTip);
        }

        _toolTip.Content = text;
    }
}
