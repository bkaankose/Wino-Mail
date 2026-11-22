using Wino.Core.Domain;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>The Contact information section: Save to, identity, then e-mail and phone (Communication) and memberships.</summary>
internal sealed partial class ContactEditView
{
    private readonly Gtk.StringList _destinationNames = Gtk.StringList.New([]);
    private readonly List<ContactCreateDestination?> _destinationItems = [];
    private Adw.ComboRow? _saveTo;
    private Adw.ActionRow? _source;
    private bool _rebuildingDestinations;

    private Gtk.Widget BuildContactInformation()
    {
        var column = Column();
        _saveTo = Adw.ComboRow.New();
        _saveTo.SetUseMarkup(false);
        _saveTo.SetTitle(Translator.ContactEditor_SaveTo);
        _saveTo.SetModel(_destinationNames);
        RegisterField("saveto", _saveTo);
        _source = Adw.ActionRow.New();
        _source.SetUseMarkup(false);
        _source.SetVisible(false);
        column.Append(Group(Translator.ContactEditor_SaveTo, Translator.ContactEditor_SaveToDescription, _saveTo, _source));

        _givenName = Field("given", Translator.ContactEditor_GivenName, nameof(ContactEditPageViewModel.GivenName), static vm => vm.GivenName, static (vm, v) => vm.GivenName = v);
        column.Append(Group(Translator.ContactEditor_Identity, Translator.ContactEditor_IdentityDescription,
            _givenName,
            Field("surname", Translator.ContactEditor_Surname, nameof(ContactEditPageViewModel.Surname), static vm => vm.Surname, static (vm, v) => vm.Surname = v),
            Field("display", Translator.ContactEditor_DisplayName, nameof(ContactEditPageViewModel.DisplayName), static vm => vm.DisplayName, static (vm, v) => vm.DisplayName = v),
            Field("nickname", Translator.ContactEditor_Nickname, nameof(ContactEditPageViewModel.Nickname), static vm => vm.Nickname, static (vm, v) => vm.Nickname = v),
            Field("fileas", Translator.ContactEditor_FileAs, nameof(ContactEditPageViewModel.FileAs), static vm => vm.FileAs, static (vm, v) => vm.FileAs = v)));
        column.Append(BuildEmails());
        column.Append(BuildPhones());
        AppendMemberships(column);
        return column;
    }

    partial void AppendMemberships(Gtk.Box column);

    partial void BindMemberships();

    private void BindIdentity()
    {
        if (_saveTo is not { } saveTo || _source is not { } source) return;
        var self = _self;
        Bind.BindCollection(ViewModel.Destinations, RebuildDestinations);
        Bind.Bind(nameof(ContactEditPageViewModel.SelectedDestination), static vm => vm.SelectedDestination, _ => RebuildDestinations());
        Bind.Bind(nameof(ContactEditPageViewModel.IsEditMode), static vm => vm.IsEditMode, editing =>
        {
            saveTo.SetVisible(!editing);
            source.SetVisible(editing);
        });
        Bind.Bind(nameof(ContactEditPageViewModel.SourceDescription), static vm => vm.SourceDescription, text => source.SetTitle(text ?? string.Empty));
        Bindings.Own(GtkSignals.OnNotify(saveTo, "selected", () => self.Value?.DestinationPicked()));
        BindMemberships();
    }

    private void RebuildDestinations()
    {
        if (_saveTo is not { } saveTo) return;
        _rebuildingDestinations = true;
        try
        {
            _destinationItems.Clear();
            var names = new List<string>();
            var selected = ViewModel.SelectedDestination;
            if (selected is null)
            {
                // "Ask every time" starts without a destination (macOS): the first entry says one is required.
                _destinationItems.Add(null);
                names.Add(Translator.ContactEditor_DestinationRequired);
            }
            foreach (var destination in ViewModel.Destinations)
            {
                if (destination is null || destination.IsReadOnly) continue;
                _destinationItems.Add(destination);
                names.Add(destination.DisplayName);
            }
            _destinationNames.Splice(0, _destinationNames.GetNItems(), [.. names]);
            var index = selected is null ? 0 : _destinationItems.FindIndex(item => item?.AddressBookId == selected.AddressBookId);
            saveTo.SetSelected(index < 0 ? 0u : (uint)index);
        }
        finally
        {
            _rebuildingDestinations = false;
        }
    }

    private void DestinationPicked()
    {
        if (_rebuildingDestinations || _saveTo is not { } saveTo) return;
        var index = (int)saveTo.GetSelected();
        if (index < 0 || index >= _destinationItems.Count || _destinationItems[index] is not { } destination) return;
        if (ViewModel.SelectedDestination?.AddressBookId == destination.AddressBookId) return;
        ViewModel.SelectedDestination = destination;
        ViewModel.MarkDirty();
    }
}
