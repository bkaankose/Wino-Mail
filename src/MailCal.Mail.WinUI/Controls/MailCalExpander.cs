using CommunityToolkit.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using WinRT;

namespace MailCal.Controls;

[ContentProperty(Name = nameof(Content))]
public partial class MailCalExpander : Control
{
    private const string PART_HeaderGrid = "HeaderGrid";
    private const string PART_ContentAreaWrapper = "ContentAreaWrapper";
    private const string PART_ContentArea = "ContentArea";

    private ContentControl HeaderGrid = null!;
    private ContentControl ContentArea = null!;
    private Grid ContentAreaWrapper = null!;

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(UIElement), typeof(MailCalExpander), new PropertyMetadata(null));
    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(nameof(Content), typeof(UIElement), typeof(MailCalExpander), new PropertyMetadata(null));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(MailCalExpander), new PropertyMetadata(false, new PropertyChangedCallback(OnIsExpandedChanged)));
    public static readonly DependencyProperty TemplateSettingsProperty = DependencyProperty.Register(nameof(TemplateSettings), typeof(MailCalExpanderTemplateSettings), typeof(MailCalExpander), new PropertyMetadata(new MailCalExpanderTemplateSettings()));

    public UIElement Content
    {
        [DynamicWindowsRuntimeCast(typeof(UIElement))]
        get { return (UIElement)GetValue(ContentProperty); }
        set { SetValue(ContentProperty, value); }
    }

    public MailCalExpanderTemplateSettings TemplateSettings
    {
        get { return (MailCalExpanderTemplateSettings)GetValue(TemplateSettingsProperty); }
        set { SetValue(TemplateSettingsProperty, value); }
    }

    public bool IsExpanded
    {
        get { return (bool)GetValue(IsExpandedProperty); }
        set { SetValue(IsExpandedProperty, value); }
    }

    public UIElement Header
    {
        [DynamicWindowsRuntimeCast(typeof(UIElement))]
        get { return (UIElement)GetValue(HeaderProperty); }
        set { SetValue(HeaderProperty, value); }
    }

    [DynamicWindowsRuntimeCast(typeof(ContentControl))]
    [DynamicWindowsRuntimeCast(typeof(Grid))]
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        HeaderGrid = (GetTemplateChild(PART_HeaderGrid) as ContentControl)!;
        ContentAreaWrapper = (GetTemplateChild(PART_ContentAreaWrapper) as Grid)!;
        ContentArea = (GetTemplateChild(PART_ContentArea) as ContentControl)!;

        Guard.IsNotNull(HeaderGrid, nameof(HeaderGrid));
        Guard.IsNotNull(ContentAreaWrapper, nameof(ContentAreaWrapper));
        Guard.IsNotNull(ContentArea, nameof(ContentArea));

        var clipComposition = ElementCompositionPreview.GetElementVisual(ContentAreaWrapper);
        clipComposition.Clip = clipComposition.Compositor.CreateInsetClip();

        ContentAreaWrapper.SizeChanged += ContentSizeChanged;

    }

    private void ContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        TemplateSettings.ContentHeight = e.NewSize.Height;
        TemplateSettings.NegativeContentHeight = -1 * (double)e.NewSize.Height;
    }

    private static void OnIsExpandedChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is MailCalExpander control)
            control.UpdateVisualStates();
    }

    private void UpdateVisualStates()
    {
        VisualStateManager.GoToState(this, IsExpanded ? "Expanded" : "Collapsed", true);
    }
}

#region Settings

public class MailCalExpanderTemplateSettings : DependencyObject
{
    public static readonly DependencyProperty HeaderHeightProperty = DependencyProperty.Register(nameof(HeaderHeight), typeof(double), typeof(MailCalExpanderTemplateSettings), new PropertyMetadata(0.0));
    public static readonly DependencyProperty ContentHeightProperty = DependencyProperty.Register(nameof(ContentHeight), typeof(double), typeof(MailCalExpanderTemplateSettings), new PropertyMetadata(0.0));
    public static readonly DependencyProperty NegativeContentHeightProperty = DependencyProperty.Register(nameof(NegativeContentHeight), typeof(double), typeof(MailCalExpanderTemplateSettings), new PropertyMetadata(0.0));

    public double NegativeContentHeight
    {
        get { return (double)GetValue(NegativeContentHeightProperty); }
        set { SetValue(NegativeContentHeightProperty, value); }
    }

    public double HeaderHeight
    {
        get { return (double)GetValue(HeaderHeightProperty); }
        set { SetValue(HeaderHeightProperty, value); }
    }

    public double ContentHeight
    {
        get { return (double)GetValue(ContentHeightProperty); }
        set { SetValue(ContentHeightProperty, value); }
    }
}

#endregion
