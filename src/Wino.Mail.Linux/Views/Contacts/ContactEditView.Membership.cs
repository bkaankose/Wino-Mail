using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Contacts;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>List and category membership as switch rows, and the contact photo with its menu.</summary>
internal sealed partial class ContactEditView
{
    private WinoRepeatedFieldGroup? _lists;
    private WinoRepeatedFieldGroup? _categories;
    private Gtk.Button? _photoButton;

    partial void AppendMemberships(Gtk.Box column)
    {
        _lists = Bindings.Own(new WinoRepeatedFieldGroup(Translator.ContactEditor_Lists, Translator.ContactEditor_ListsDescription, null));
        _categories = Bindings.Own(new WinoRepeatedFieldGroup(Translator.SettingsMailCategories_Title, Translator.ContactEditor_CategoriesDescription, null));
        _lists.Root.SetVisible(false);
        _categories.Root.SetVisible(false);
        column.Append(_lists.Root);
        column.Append(_categories.Root);
    }

    partial void BindMemberships()
    {
        var self = _self;
        if (_lists is { } lists)
        {
            Rows(lists, ViewModel.ListMemberships, (membership, index, scope) =>
                MemberSwitch(scope, "list" + Key(index), membership.Name, membership.IsMember, value => membership.IsMember = value),
                () => lists.Root.SetVisible(self.Value?.ViewModel.HasLists == true));
        }
        if (_categories is { } categories)
        {
            Rows(categories, ViewModel.CategoryMemberships, (membership, index, scope) =>
                MemberSwitch(scope, "category" + Key(index), membership.Name, membership.IsMember, value => membership.IsMember = value),
                () => categories.Root.SetVisible(self.Value?.ViewModel.HasCategories == true));
        }
        if (_photoButton is { } photo)
        {
            Bind.Bind(nameof(ContactEditPageViewModel.SelectedDestination), static vm => vm.CanEditPhoto, photo.SetVisible);
            Bindings.Own(GtkSignals.OnClicked(photo, () => self.Value?.ShowPhotoMenu()));
        }
        Bind.BindAny(static vm => (vm.PreviewPhotoBytes, vm.PreviewPhotoPath), state =>
        {
            var texture = state.PreviewPhotoBytes is { Length: > 0 } bytes ? Textures.FromBytes(bytes)
                : !string.IsNullOrWhiteSpace(state.PreviewPhotoPath) ? Textures.FromFile(state.PreviewPhotoPath) : null;
            _picture.Picture = texture;
        });
    }

    partial void BuildPhotoButton()
    {
        var button = Gtk.Button.New();
        button.SetChild(WinoIcons.Label(WinoIconGlyph.More, 14));
        button.AddCssClass("circular");
        button.AddCssClass("osd");
        button.SetHalign(Gtk.Align.End);
        button.SetValign(Gtk.Align.End);
        button.SetSizeRequest(28, 28);
        WinoA11y.LabelWithTooltip(button, Translator.ContactAction_PhotoOptions);
        WinoA11y.HasPopup(button);
        RegisterField("photo", button);
        _pictureOverlay.AddOverlay(button);
        _photoButton = button;
    }

    internal void ShowPhotoMenu()
    {
        if (_photoButton is not { } anchor || IsReleased) return;
        var self = _self;
        var builder = WinoMenus.Create()
            .Item(Translator.ContactAction_ChangePhoto, () => self.Value?.Observe(self.Value.ViewModel.ChoosePhotoCommand.ExecuteAsync(null)), icon: WinoIconGlyph.Camera)
            .Item(ViewModel.RemovePhotoLabel ?? Translator.ContactEditDialog_RemovePhoto, () => self.Value?.ViewModel.RemovePhotoCommand.Execute(null),
                icon: WinoIconGlyph.Delete, destructive: true);
        ContextMenu.Show(anchor, builder.Build());
    }

    private Adw.SwitchRow MemberSwitch(Wino.Presentation.Bindings.BindingScope scope, string key, string? title, bool isMember, Action<bool> set)
    {
        var row = Adw.SwitchRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(title ?? string.Empty);
        row.SetActive(isMember);
        RegisterField(key, row);
        var self = _self;
        scope.Own(GtkSignals.OnNotify(row, "active", () =>
        {
            set(row.GetActive());
            self.Value?.ViewModel.MarkDirty();
        }));
        return row;
    }
}
