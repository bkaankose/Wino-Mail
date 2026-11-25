using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.Controls.Linux.Contacts;
using Wino.Mail.ViewModels;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>Work and relations, the three postal addresses, personal data with instant messaging and family, and notes.</summary>
internal sealed partial class ContactEditView
{
    private Gtk.Box? _addressColumn;
    private WinoRepeatedFieldGroup? _family;
    private Gtk.TextView? _notes;

    private Gtk.Widget BuildWork()
    {
        var column = Column();
        column.Append(Group(Translator.ContactEditor_Work, Translator.ContactEditor_WorkDescription,
            Field("company", Translator.ContactEditor_Company, nameof(ContactEditPageViewModel.CompanyName), static vm => vm.CompanyName, static (vm, v) => vm.CompanyName = v),
            Field("department", Translator.ContactEditor_Department, nameof(ContactEditPageViewModel.Department), static vm => vm.Department, static (vm, v) => vm.Department = v),
            Field("job", Translator.ContactEditor_JobTitle, nameof(ContactEditPageViewModel.JobTitle), static vm => vm.JobTitle, static (vm, v) => vm.JobTitle = v),
            Field("office", Translator.ContactEditor_Office, nameof(ContactEditPageViewModel.OfficeLocation), static vm => vm.OfficeLocation, static (vm, v) => vm.OfficeLocation = v)));
        column.Append(Group(Translator.ContactEditor_Relations, Translator.ContactEditor_RelationsDescription,
            Field("manager", Translator.ContactEditor_Manager, nameof(ContactEditPageViewModel.ManagerName), static vm => vm.ManagerName, static (vm, v) => vm.ManagerName = v),
            Field("assistant", Translator.ContactEditor_Assistant, nameof(ContactEditPageViewModel.AssistantName), static vm => vm.AssistantName, static (vm, v) => vm.AssistantName = v)));
        return column;
    }

    private Gtk.Widget BuildAddress()
    {
        var column = Column();
        column.Append(Group(Translator.ContactEditor_Addresses, Translator.ContactEditor_AddressesDescription));
        _addressColumn = column;
        return column;
    }

    private Gtk.Widget BuildOther()
    {
        var column = Column();
        column.Append(Group(Translator.ContactEditor_Personal, Translator.ContactEditor_PersonalDescription,
            IntField("birthday", Translator.ContactEditor_BirthDay, nameof(ContactEditPageViewModel.BirthdayDay), static vm => vm.BirthdayDay, static (vm, v) => vm.BirthdayDay = v),
            IntField("birthmonth", Translator.ContactEditor_BirthMonth, nameof(ContactEditPageViewModel.BirthdayMonth), static vm => vm.BirthdayMonth, static (vm, v) => vm.BirthdayMonth = v),
            IntField("birthyear", Translator.ContactEditor_BirthYear, nameof(ContactEditPageViewModel.BirthdayYear), static vm => vm.BirthdayYear, static (vm, v) => vm.BirthdayYear = v),
            Field("website", Translator.ContactEditor_Website, nameof(ContactEditPageViewModel.Website), static vm => vm.Website, static (vm, v) => vm.Website = v, Gtk.InputPurpose.Url)));
        column.Append(BuildInstantMessaging());
        _family = Bindings.Own(new WinoRepeatedFieldGroup(Translator.ContactEditor_Relations, Translator.ContactEditor_RelationsDescription, Translator.ContactEditor_AddChild));
        _family.Insert(Field("spouse", Translator.ContactEditor_Spouse, nameof(ContactEditPageViewModel.SpouseName), static vm => vm.SpouseName, static (vm, v) => vm.SpouseName = v), 0);
        column.Append(_family.Root);
        return column;
    }

    private Gtk.Widget BuildNotes()
    {
        var column = Column();
        _notes = Gtk.TextView.New();
        _notes.SetWrapMode(Gtk.WrapMode.WordChar);
        _notes.SetAcceptsTab(false);
        _notes.SetTopMargin(8);
        _notes.SetBottomMargin(8);
        _notes.SetLeftMargin(8);
        _notes.SetRightMargin(8);
        WinoA11y.Label(_notes, Translator.ContactEditor_Notes);
        RegisterField("notes", _notes);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetMinContentHeight(180);
        scroller.AddCssClass("card");
        scroller.SetChild(_notes);
        var group = Group(Translator.ContactEditor_Notes, Translator.ContactEditor_NotesDescription);
        group.Add(scroller);
        column.Append(group);
        return column;
    }

    partial void BindDetails()
    {
        var self = _self;
        if (_addressColumn is { } addresses)
        {
            var parent = Bindings.Own(new BindingScope());
            var shown = new Dictionary<ContactPostalAddress, (Gtk.Widget Group, BindingScope Scope)>(ReferenceEqualityComparer.Instance);
            Bind.BindCollection(ViewModel.PostalAddresses, () =>
            {
                if (self.Value is not { } view) return;
                var current = new HashSet<ContactPostalAddress>(view.ViewModel.PostalAddresses.Where(static a => a is not null), ReferenceEqualityComparer.Instance);
                foreach (var gone in shown.Keys.Where(a => !current.Contains(a)).ToArray())
                {
                    addresses.Remove(shown[gone].Group);
                    shown[gone].Scope.Dispose();
                    shown.Remove(gone);
                }
                var index = 0;
                foreach (var address in view.ViewModel.PostalAddresses)
                {
                    if (address is null) continue;
                    var key = Key(index++);
                    if (shown.ContainsKey(address)) continue;
                    var scope = parent.Own(new BindingScope());
                    var group = Group(address.Kind.ToString(), null,
                        view.EntityField(scope, "street" + key, Translator.ContactEditor_Street, address.Street, value => address.Street = value),
                        view.EntityField(scope, "city" + key, Translator.ContactEditor_City, address.City, value => address.City = value),
                        view.EntityField(scope, "region" + key, Translator.ContactEditor_Region, address.Region, value => address.Region = value),
                        view.EntityField(scope, "postal" + key, Translator.ContactEditor_PostalCode, address.PostalCode, value => address.PostalCode = value),
                        view.EntityField(scope, "country" + key, Translator.ContactEditor_Country, address.Country, value => address.Country = value));
                    addresses.Append(group);
                    shown[address] = (group, scope);
                }
            });
        }
        if (_family is { } family)
        {
            family.AddRequested += () => self.Value?.ViewModel.AddChildCommand.Execute(null);
            // The spouse row stays first; children follow it.
            Rows(family, ViewModel.Relations, (relation, index, scope) =>
            {
                var key = Key(index);
                var row = EntityField(scope, "child" + key, Translator.ContactEditor_Child, relation.Name, value => relation.Name = value);
                row.AddSuffix(RemoveButton(scope, "removechild" + key, () => self.Value?.ViewModel.RemoveRelationCommand.Execute(relation)));
                return row;
            }, offset: 1);
        }
        if (_notes is { } notes)
        {
            var buffer = notes.GetBuffer();
            var binding = Bindings.Own(new PropertyBinding<ContactEditPageViewModel, string?>(ViewModel, nameof(ContactEditPageViewModel.Notes),
                static vm => vm.Notes, text =>
                {
                    var value = text ?? string.Empty;
                    if (!string.Equals(BufferText(buffer), value, StringComparison.Ordinal)) buffer.SetText(value, -1);
                }, Dispatcher, ReportError, static (vm, text) => vm.Notes = text ?? string.Empty));
            Bindings.Own(GtkSignals.Connect<Gtk.TextBuffer>(h => buffer.OnChanged += h, h => buffer.OnChanged -= h, (_, _) =>
            {
                if (self.Value is not { } view) return;
                var text = BufferText(buffer);
                if (string.Equals(view.ViewModel.Notes ?? string.Empty, text, StringComparison.Ordinal)) return;
                binding.UpdateSource(text);
                view.ViewModel.MarkDirty();
            }));
        }
    }

    internal static string BufferText(Gtk.TextBuffer buffer)
    {
        buffer.GetBounds(out var start, out var end);
        return buffer.GetText(start, end, false);
    }
}
