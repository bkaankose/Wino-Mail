using Wino.Core.Domain;
using Wino.Mail.Linux.Views.Links;
using Wino.Mail.ViewModels.Calendar;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>The mail an event came from and its follow-up tasks, in a card under the detail rows (Windows linked items, macOS linked card).</summary>
internal sealed partial class EventDetailsView
{
    private readonly Gtk.Box _linksCard = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private LinkedItemStrip? _links;

    internal LinkedItemStrip? Links => _links;

    private void BuildLinks()
    {
        var strip = _links = new LinkedItemStrip(item => Observe(ViewModel.OpenLinkedItemCommand.ExecuteAsync(item)), Gtk.Orientation.Vertical,
            Translator.ConnectedItems_LinkedHeader);
        strip.Root.SetMarginTop(10);
        strip.Root.SetMarginBottom(10);
        strip.Root.SetMarginStart(12);
        strip.Root.SetMarginEnd(12);
        _linksCard.AddCssClass("card");
        _linksCard.Append(strip.Root);
        _linksCard.SetVisible(false);
        _column.Append(_linksCard);
    }

    private void InitializeLinks()
    {
        if (_links is not { } strip) return;
        strip.Attach(ViewModel.LinkedItems);
        Bindings.Own(strip);
        Bind.Bind(nameof(EventDetailsPageViewModel.HasLinkedItems), static vm => vm.HasLinkedItems, _linksCard.SetVisible);
    }
}
