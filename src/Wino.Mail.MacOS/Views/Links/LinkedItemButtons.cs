using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AppKit;
using Wino.Core.Domain;
using Wino.Mail.Controls.AppKit.ToDo;
using Wino.Mail.ViewModels.Links;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Links;

/// <summary>
/// The mails, events and tasks an open item is linked to (Windows linked items list). Each entry is a flat button with the
/// item's glyph and title; the buttons follow the shared collection and are rebuilt when it changes.
/// </summary>
internal sealed class LinkedItemButtons : NSStackView
{
    private readonly Action<LinkedItemViewModel> _open;
    private ObservableCollection<LinkedItemViewModel>? _items;

    public LinkedItemButtons(Action<LinkedItemViewModel> open, NSUserInterfaceLayoutOrientation orientation)
    {
        _open = open;
        Orientation = orientation;
        Spacing = orientation == NSUserInterfaceLayoutOrientation.Horizontal ? 4 : 2;
        Alignment = NSLayoutAttribute.Leading;
        DetachesHiddenViews = true;
        TranslatesAutoresizingMaskIntoConstraints = false;
    }

    public void Attach(ObservableCollection<LinkedItemViewModel> items)
    {
        if (ReferenceEquals(_items, items)) return;
        Detach();
        _items = items;
        _items.CollectionChanged += ItemsChanged;
        Rebuild();
    }

    public void Detach()
    {
        if (_items is null) return;
        _items.CollectionChanged -= ItemsChanged;
        _items = null;
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) => Rebuild();

    private void Rebuild()
    {
        foreach (var view in ArrangedSubviews)
        {
            RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }

        if (_items is null) return;
        foreach (var item in _items)
            AddArrangedSubview(CreateButton(item));
    }

    private NSButton CreateButton(LinkedItemViewModel item)
    {
        var button = new NSButton
        {
            Title = item.Title,
            Image = WinoToDoStyle.MonoImage(item.Glyph, 13, WinoStyle.SecondaryText),
            ImagePosition = NSCellImagePosition.ImageLeading,
            BezelStyle = NSBezelStyle.Inline,
            Font = NSFont.SystemFontOfSize(12),
            ToolTip = item.HasSubtitle ? item.Subtitle : null,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        button.Activated += (_, _) => _open(item);
        WinoAccessibility.Label(button, item.AutomationName);
        return button;
    }
}
