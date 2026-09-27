using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.IntelligenceProgressRing;
using Windows.ApplicationModel.DataTransfer;

namespace Wino.Mail.Controls.IntelligenceHeader;

/// <summary>
/// The rewrite feature. Like translation it is host-driven: the header raises
/// <see cref="WinoIntelligenceAction.Rewrite"/>, <see cref="WinoIntelligenceAction.CancelRewrite"/>
/// and <see cref="WinoIntelligenceAction.RegenerateRewrite"/>, and the host reports progress and
/// results back through the rewrite properties. The host decides whether a result is shown in
/// place of the original message.
/// </summary>
[TemplatePart(Name = PartRewriteChipButtonName, Type = typeof(Button))]
[TemplatePart(Name = PartRewriteCancelButtonName, Type = typeof(Button))]
[TemplatePart(Name = PartRewriteChipLabelName, Type = typeof(TextBlock))]
[TemplatePart(Name = PartRewriteChipHintName, Type = typeof(TextBlock))]
[TemplatePart(Name = PartRewriteResultDotName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartRewritePanelName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartRewriteModeComboBoxName, Type = typeof(ComboBox))]
[TemplatePart(Name = PartRewriteModeDescriptionTextBlockName, Type = typeof(TextBlock))]
[TemplatePart(Name = PartRewriteRunButtonName, Type = typeof(Button))]
[TemplatePart(Name = PartRewriteStatusTextBlockName, Type = typeof(TextBlock))]
[TemplatePart(Name = PartRewriteBusyTextBlockName, Type = typeof(TextBlock))]
[TemplatePart(Name = PartRewriteAppliedPanelName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartRewriteWaitingPanelName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartRewriteWaitingRingName, Type = typeof(WinoIntelligenceProgressRing))]
[TemplatePart(Name = PartRewriteCopyButtonName, Type = typeof(Button))]
[TemplatePart(Name = PartRewriteRegenerateButtonName, Type = typeof(Button))]
[TemplatePart(Name = PartRewriteCloseButtonName, Type = typeof(Button))]
public sealed partial class WinoIntelligenceHeader
{
    private const string PartRewriteChipButtonName = "PART_RewriteChipButton";
    private const string PartRewriteCancelButtonName = "PART_RewriteCancelButton";
    private const string PartRewriteChipLabelName = "PART_RewriteChipLabel";
    private const string PartRewriteChipHintName = "PART_RewriteChipHint";
    private const string PartRewriteResultDotName = "PART_RewriteResultDot";
    private const string PartRewritePanelName = "PART_RewritePanel";
    private const string PartRewriteModeComboBoxName = "PART_RewriteModeComboBox";
    private const string PartRewriteModeDescriptionTextBlockName = "PART_RewriteModeDescriptionTextBlock";
    private const string PartRewriteRunButtonName = "PART_RewriteRunButton";
    private const string PartRewriteStatusTextBlockName = "PART_RewriteStatusTextBlock";
    private const string PartRewriteBusyTextBlockName = "PART_RewriteBusyTextBlock";
    private const string PartRewriteAppliedPanelName = "PART_RewriteAppliedPanel";
    private const string PartRewriteWaitingPanelName = "PART_RewriteWaitingPanel";
    private const string PartRewriteWaitingRingName = "PART_RewriteWaitingRing";
    private const string PartRewriteCopyButtonName = "PART_RewriteCopyButton";
    private const string PartRewriteRegenerateButtonName = "PART_RewriteRegenerateButton";
    private const string PartRewriteCloseButtonName = "PART_RewriteCloseButton";

    private FeatureParts? _rewriteParts;
    private FrameworkElement? _rewritePanel;
    private ComboBox? _rewriteModeComboBox;
    private TextBlock? _rewriteModeDescriptionTextBlock;
    private Button? _rewriteRunButton;
    private TextBlock? _rewriteStatusTextBlock;
    private TextBlock? _rewriteBusyTextBlock;
    private FrameworkElement? _rewriteAppliedPanel;
    private FrameworkElement? _rewriteWaitingPanel;
    private WinoIntelligenceProgressRing? _rewriteWaitingRing;
    private Button? _rewriteCopyButton;
    private Button? _rewriteRegenerateButton;
    private Button? _rewriteCloseButton;
    private bool _isSynchronizingRewriteModes;

    /// <summary>Gets or sets a value indicating whether the rewrite action is offered.</summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsRewriteAvailable { get; set; }

    /// <summary>Gets or sets the rewrite chip and run-button text.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Rewrite")]
    public partial string RewriteButtonText { get; set; }

    /// <summary>Gets or sets the rewrite panel heading.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Rewrite message")]
    public partial string RewritePanelHeadingText { get; set; }

    /// <summary>Gets or sets the accessible label of the rewrite-mode picker.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Rewrite tone")]
    public partial string RewriteModeLabel { get; set; }

    /// <summary>Gets or sets the rewrite modes the host supports.</summary>
    [GeneratedDependencyProperty]
    public partial IEnumerable<WinoIntelligenceRewriteModeOption>? RewriteModes { get; set; }

    /// <summary>Gets or sets the selected rewrite mode identifier.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SelectedRewriteMode { get; set; }

    /// <summary>Gets or sets a value indicating whether a rewrite request is running.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsRewriteBusy { get; set; }

    /// <summary>Gets or sets a value indicating whether the host holds a rewrite result.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool HasRewriteResult { get; set; }

    /// <summary>Gets or sets a value indicating whether the rewrite replaces the original on screen.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsRewriteApplied { get; set; }

    /// <summary>Gets or sets the rewrite progress or result status text.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string RewriteStatusText { get; set; }

    /// <summary>Gets or sets the plain text of the latest rewrite, used by the copy action.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string RewriteResultText { get; set; }

    partial void OnIsRewriteAvailableChanged(bool newValue) => OnStatePropertyChanged(this, IsRewriteAvailableProperty);
    partial void OnRewriteButtonTextChanged(string newValue) => SyncRewriteVisuals();
    partial void OnRewritePanelHeadingTextChanged(string newValue) => SyncRewriteVisuals();
    partial void OnRewriteModeLabelChanged(string newValue) => SyncRewriteVisuals();
    partial void OnRewriteModesChanged(IEnumerable<WinoIntelligenceRewriteModeOption>? newValue) => SyncRewriteVisuals();
    partial void OnSelectedRewriteModeChanged(string newValue) => SyncRewriteVisuals();
    partial void OnIsRewriteBusyChanged(bool newValue) => SyncRewriteVisuals();
    partial void OnHasRewriteResultChanged(bool newValue) => SyncRewriteVisuals();
    partial void OnIsRewriteAppliedChanged(bool newValue) => SyncRewriteVisuals();
    partial void OnRewriteStatusTextChanged(string newValue) => SyncRewriteVisuals();
    partial void OnRewriteResultTextChanged(string newValue) => SyncRewriteVisuals();

    private static WinoIntelligenceFeature RewriteFeature => (WinoIntelligenceFeature)(-2);

    private void GetRewriteTemplateParts()
    {
        _rewriteParts = GetFeatureParts(PartRewriteChipButtonName, PartRewriteCancelButtonName, PartRewriteChipLabelName, PartRewriteChipHintName, PartRewriteResultDotName);
        _rewritePanel = GetTemplateChild(PartRewritePanelName) as FrameworkElement;
        _rewriteModeComboBox = GetTemplateChild(PartRewriteModeComboBoxName) as ComboBox;
        _rewriteModeDescriptionTextBlock = GetTemplateChild(PartRewriteModeDescriptionTextBlockName) as TextBlock;
        _rewriteRunButton = GetTemplateChild(PartRewriteRunButtonName) as Button;
        _rewriteStatusTextBlock = GetTemplateChild(PartRewriteStatusTextBlockName) as TextBlock;
        _rewriteBusyTextBlock = GetTemplateChild(PartRewriteBusyTextBlockName) as TextBlock;
        _rewriteAppliedPanel = GetTemplateChild(PartRewriteAppliedPanelName) as FrameworkElement;
        _rewriteWaitingPanel = GetTemplateChild(PartRewriteWaitingPanelName) as FrameworkElement;
        _rewriteWaitingRing = GetTemplateChild(PartRewriteWaitingRingName) as WinoIntelligenceProgressRing;
        _rewriteCopyButton = GetTemplateChild(PartRewriteCopyButtonName) as Button;
        _rewriteRegenerateButton = GetTemplateChild(PartRewriteRegenerateButtonName) as Button;
        _rewriteCloseButton = GetTemplateChild(PartRewriteCloseButtonName) as Button;
    }

    private void AttachRewriteHandlers()
    {
        AttachFeatureHandlers(_rewriteParts, OnRewriteChipClicked, OnRewriteCancelClicked);
        if (_rewriteModeComboBox is not null) _rewriteModeComboBox.SelectionChanged += OnRewriteModeChanged;
        if (_rewriteRunButton is not null) _rewriteRunButton.Click += OnRewriteRunClicked;
        if (_rewriteCopyButton is not null) _rewriteCopyButton.Click += OnRewriteCopyClicked;
        if (_rewriteRegenerateButton is not null) _rewriteRegenerateButton.Click += OnRewriteRegenerateClicked;
        if (_rewriteCloseButton is not null) _rewriteCloseButton.Click += OnRewriteCloseClicked;
    }

    private void DetachRewriteHandlers()
    {
        DetachFeatureHandlers(_rewriteParts, OnRewriteChipClicked, OnRewriteCancelClicked);
        if (_rewriteModeComboBox is not null) _rewriteModeComboBox.SelectionChanged -= OnRewriteModeChanged;
        if (_rewriteRunButton is not null) _rewriteRunButton.Click -= OnRewriteRunClicked;
        if (_rewriteCopyButton is not null) _rewriteCopyButton.Click -= OnRewriteCopyClicked;
        if (_rewriteRegenerateButton is not null) _rewriteRegenerateButton.Click -= OnRewriteRegenerateClicked;
        if (_rewriteCloseButton is not null) _rewriteCloseButton.Click -= OnRewriteCloseClicked;
    }

    private void ResetRewriteState()
    {
        IsRewriteBusy = false;
        HasRewriteResult = false;
        IsRewriteApplied = false;
        RewriteStatusText = string.Empty;
        RewriteResultText = string.Empty;
    }

    private void SyncRewriteVisuals()
    {
        var modes = RewriteModes?.ToArray() ?? [];
        var selected = modes.FirstOrDefault(x => string.Equals(x.Mode, SelectedRewriteMode, StringComparison.OrdinalIgnoreCase));

        SyncFeature(
            _rewriteParts,
            IsRewriteBusy ? WinoIntelligenceFeatureState.Busy
                : HasRewriteResult ? WinoIntelligenceFeatureState.Done
                : WinoIntelligenceFeatureState.Idle,
            IsRewriteAvailable,
            RewriteButtonText,
            string.Empty,
            RewriteStatusText,
            CancelButtonText);

        _isSynchronizingRewriteModes = true;
        try
        {
            if (_rewriteModeComboBox is not null)
            {
                _rewriteModeComboBox.ItemsSource = modes;
                _rewriteModeComboBox.SelectedItem = selected;
                _rewriteModeComboBox.IsEnabled = !IsRewriteBusy;
                AutomationProperties.SetName(_rewriteModeComboBox, RewriteModeLabel);
            }
        }
        finally { _isSynchronizingRewriteModes = false; }

        if (_rewriteModeDescriptionTextBlock is not null)
        {
            _rewriteModeDescriptionTextBlock.Text = selected?.Description ?? string.Empty;
            _rewriteModeDescriptionTextBlock.Visibility = ToVisibility(!string.IsNullOrWhiteSpace(selected?.Description));
        }
        if (_rewriteRunButton is not null)
        {
            _rewriteRunButton.Content = IsRewriteApplied ? ShowOriginalButtonText : RewriteButtonText;
            AutomationProperties.SetName(_rewriteRunButton, IsRewriteApplied ? ShowOriginalButtonText : RewriteButtonText);
            _rewriteRunButton.IsEnabled = !IsRewriteBusy && (IsRewriteApplied || selected is not null);
        }

        var canUseResult = HasRewriteResult && !IsRewriteBusy;
        if (_rewriteCopyButton is not null) _rewriteCopyButton.Visibility = ToVisibility(canUseResult && !string.IsNullOrWhiteSpace(RewriteResultText));
        if (_rewriteRegenerateButton is not null) _rewriteRegenerateButton.Visibility = ToVisibility(canUseResult);
        if (_rewriteStatusTextBlock is not null) _rewriteStatusTextBlock.Text = RewriteStatusText;
        if (_rewriteBusyTextBlock is not null) _rewriteBusyTextBlock.Text = RewriteStatusText;

        SyncPanels();
    }

    /// <summary>Called from <see cref="SyncPanels"/> so the rewrite rows follow the open-panel rule.</summary>
    private void SyncRewritePanel()
    {
        var rewriteOpen = _openPanel == RewriteFeature;
        SetPanel(_rewritePanel, rewriteOpen);

        var rewriteBusy = rewriteOpen && IsRewriteBusy;
        if (_rewriteWaitingPanel is not null) _rewriteWaitingPanel.Visibility = ToVisibility(rewriteBusy);
        if (_rewriteWaitingRing is not null) _rewriteWaitingRing.IsActive = rewriteBusy;
        if (_rewriteAppliedPanel is not null)
        {
            _rewriteAppliedPanel.Visibility = ToVisibility(rewriteOpen && HasRewriteResult && !IsRewriteBusy
                && !string.IsNullOrWhiteSpace(RewriteStatusText));
        }
    }

    private void OnRewriteChipClicked(object sender, RoutedEventArgs e)
    {
        if (!IsRewriteAvailable) return;
        OpenPanel(RewriteFeature, _rewriteCloseButton);
    }

    private void OnRewriteCancelClicked(object sender, RoutedEventArgs e)
    {
        if (!IsRewriteBusy) return;
        ActionInvoked?.Invoke(this, new WinoIntelligenceActionEventArgs(WinoIntelligenceAction.CancelRewrite));
        Announce(CancelButtonText);
        ClosePanel(RewriteFeature, _rewriteParts?.MainButton);
    }

    private void OnRewriteRunClicked(object sender, RoutedEventArgs e)
    {
        if (!IsRewriteAvailable || IsRewriteBusy) return;
        ActionInvoked?.Invoke(this, new WinoIntelligenceActionEventArgs(WinoIntelligenceAction.Rewrite));
    }

    private void OnRewriteRegenerateClicked(object sender, RoutedEventArgs e)
    {
        if (!IsRewriteAvailable || IsRewriteBusy) return;
        ActionInvoked?.Invoke(this, new WinoIntelligenceActionEventArgs(WinoIntelligenceAction.RegenerateRewrite));
    }

    private void OnRewriteCloseClicked(object sender, RoutedEventArgs e)
        => ClosePanel(RewriteFeature, _rewriteParts?.MainButton);

    private void OnRewriteCopyClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RewriteResultText)) return;
        var package = new DataPackage();
        package.SetText(RewriteResultText);
        Clipboard.SetContent(package);
    }

    private void OnRewriteModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isSynchronizingRewriteModes && _rewriteModeComboBox?.SelectedItem is WinoIntelligenceRewriteModeOption mode)
            SelectedRewriteMode = mode.Mode;
    }
}
