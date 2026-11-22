using System.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.Contacts;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Controls;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>A recycled contact list cell: maps the bound contact into <see cref="WinoContactRow"/>.</summary>
internal sealed class ContactCell : IWinoCell<AccountContactViewModel>
{
    private readonly ContactPictures _pictures;
    private readonly IDispatcher _dispatcher;

    public ContactCell(ContactPictures pictures, IDispatcher dispatcher, Action<AccountContactViewModel> toggleFavorite)
    {
        _pictures = pictures;
        _dispatcher = dispatcher;
        Row = new WinoContactRow();
        Row.FavoriteToggled += () =>
        {
            if (Item is { } contact) toggleFavorite(contact);
        };
    }

    public Gtk.Widget Root => Row.Root;

    public WinoContactRow Row { get; }

    public AccountContactViewModel? Item { get; private set; }

    /// <summary>The position in the sections model at the last bind.</summary>
    public uint Position { get; private set; }

    public Gtk.ListItem? ListItem { get; private set; }

    public void Bind(AccountContactViewModel item, WinoCellBinding binding)
    {
        Item = item;
        ListItem = binding.ListItem;
        Position = binding.Position;
        binding.ListItem.SetAccessibleLabel(item.Name ?? item.Address ?? string.Empty);
        Refresh();
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (!WinoContactRowRules.RefreshesRow(args.PropertyName)) return;
            _ = _dispatcher.ExecuteOnUIThread(() =>
            {
                if (ReferenceEquals(Item, item)) Refresh();
            });
        };
        item.PropertyChanged += changed;
        binding.Scope.Own(new ActionDisposable(() => item.PropertyChanged -= changed));
    }

    public void Unbind()
    {
        Item = null;
        ListItem = null;
    }

    public void Refresh()
    {
        if (Item is not { } item) return;
        Row.Apply(new WinoContactRowModel(item.Name, item.SecondaryValue, item.SourceLabel, item.Address, item.IsFavorite,
            Categories(item), _pictures.Get(item)));
    }

    // item.Categories allocates a collection per call; the source list is read instead.
    private static MailCategory[] Categories(AccountContactViewModel item)
        => item.SourceContact?.Categories is { Count: > 0 } categories ? [.. categories] : [];
}
