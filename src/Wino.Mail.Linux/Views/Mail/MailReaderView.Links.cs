using Wino.Core.Domain;
using Wino.Mail.Linux.Views.Links;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>Tasks and events created from the open message, under the header (Windows linked items panel, macOS linked row).</summary>
internal sealed partial class MailReaderView
{
    private LinkedItemStrip? _links;

    internal LinkedItemStrip? Links => _links;

    partial void BuildLinks()
    {
        var strip = _links = new LinkedItemStrip(item => Observe(ViewModel.OpenLinkedItemCommand.ExecuteAsync(item)), Gtk.Orientation.Horizontal,
            Translator.ConnectedItems_LinkedHeader);
        _linksSlot.SetChild(strip.Root);
    }

    partial void InitializeLinks()
    {
        if (_links is not { } strip) return;
        strip.Attach(ViewModel.LinkedItems);
        Bindings.Own(strip);
        Bind.Bind(nameof(MailRenderingPageViewModel.HasLinkedItems), static vm => vm.HasLinkedItems, _linksSlot.SetVisible);
#if DEBUG
        WhenDescribing(() => $"links visible={_linksSlot.GetVisible()} items=[{strip.Describe().Replace('\n', ';')}]");
#endif
    }
}
