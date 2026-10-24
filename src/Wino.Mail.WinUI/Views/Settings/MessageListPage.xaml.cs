using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class MessageListPage : MessageListPageAbstract
{
    public MessageListPage()
    {
        this.InitializeComponent();

        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        CompositionTarget.Rendered += OnFirstFrameRendered;
    }

    private void OnFirstFrameRendered(object? sender, RenderedEventArgs e)
    {
        CompositionTarget.Rendered -= OnFirstFrameRendered;

        if (!IsPreparedForClose)
            FindName(nameof(DeferredSettingsPanel));
    }

    protected override void ReleasePageResources()
    {
        Loaded -= OnPageLoaded;
        CompositionTarget.Rendered -= OnFirstFrameRendered;
        ReleaseTemplateSelector("PreviewMailItemTemplateSelector");
    }
}
