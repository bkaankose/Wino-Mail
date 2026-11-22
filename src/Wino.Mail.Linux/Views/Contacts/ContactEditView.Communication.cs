using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Contacts;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>E-mail addresses (at most three), phones and instant messaging addresses as repeated rows.</summary>
internal sealed partial class ContactEditView
{
    private static readonly string[] PhoneKinds = Enum.GetNames<ContactPhoneKind>();

    private WinoRepeatedFieldGroup? _emails;
    private WinoRepeatedFieldGroup? _phones;
    private WinoRepeatedFieldGroup? _ims;

    private Gtk.Widget BuildEmails()
    {
        _emails = Bindings.Own(new WinoRepeatedFieldGroup(Translator.ContactEditor_Emails, Translator.ContactEditor_EmailsDescription, Translator.ContactEditor_AddEmail));
        return _emails.Root;
    }

    private Gtk.Widget BuildPhones()
    {
        _phones = Bindings.Own(new WinoRepeatedFieldGroup(Translator.ContactEditor_Phones, Translator.ContactEditor_PhonesDescription, Translator.ContactEditor_AddPhone));
        return _phones.Root;
    }

    private Gtk.Widget BuildInstantMessaging()
    {
        _ims = Bindings.Own(new WinoRepeatedFieldGroup(Translator.ContactEditor_InstantMessaging, Translator.ContactEditor_InstantMessagingDescription, Translator.ContactEditor_AddIm));
        return _ims.Root;
    }

    private void BindCommunication()
    {
        var self = _self;
        if (_emails is { } emails)
        {
            emails.AddRequested += () => self.Value?.ViewModel.AddEmailCommand.Execute(null);
            Rows(emails, ViewModel.EmailAddresses, (email, index, scope) =>
            {
                var key = Key(index);
                var row = EntityField(scope, "email" + key, Translator.ContactEditor_Email, email.Address, value => email.Address = value, Gtk.InputPurpose.Email);
                row.AddSuffix(SuffixEntry(scope, "emaillabel" + key, Translator.ContactEditor_EmailLabel, email.Label, value => email.Label = value));
                row.AddSuffix(RemoveButton(scope, "removeemail" + key, () => self.Value?.ViewModel.RemoveEmailCommand.Execute(email)));
                return row;
            }, () =>
            {
                if (self.Value is { } view) emails.CanAdd = view.ViewModel.EmailAddresses.Count < 3;
            });
        }
        if (_phones is { } phones)
        {
            phones.AddRequested += () => self.Value?.ViewModel.AddPhoneCommand.Execute(null);
            Rows(phones, ViewModel.PhoneNumbers, (phone, index, scope) =>
            {
                var key = Key(index);
                var row = EntityField(scope, "phone" + key, Translator.ContactEditor_Phone, phone.Number, value => phone.Number = value, Gtk.InputPurpose.Phone);
                var kind = Gtk.DropDown.NewFromStrings(PhoneKinds);
                kind.SetValign(Gtk.Align.Center);
                kind.SetSelected((uint)Math.Clamp((int)phone.Kind, 0, PhoneKinds.Length - 1));
                WinoA11y.Label(kind, Translator.ContactEditor_PhoneKind);
                RegisterField("phonekind" + key, kind);
                scope.Own(GtkSignals.OnNotify(kind, "selected", () =>
                {
                    var selected = (int)kind.GetSelected();
                    if (selected < 0 || selected >= PhoneKinds.Length || (int)phone.Kind == selected) return;
                    phone.Kind = (ContactPhoneKind)selected;
                    self.Value?.ViewModel.MarkDirty();
                }));
                row.AddSuffix(kind);
                row.AddSuffix(RemoveButton(scope, "removephone" + key, () => self.Value?.ViewModel.RemovePhoneCommand.Execute(phone)));
                return row;
            });
        }
        if (_ims is { } ims)
        {
            ims.AddRequested += () => self.Value?.ViewModel.AddImAddressCommand.Execute(null);
            Rows(ims, ViewModel.ImAddresses, (im, index, scope) =>
            {
                var key = Key(index);
                var row = EntityField(scope, "im" + key, Translator.ContactEditor_InstantMessaging, im.Address, value => im.Address = value);
                row.AddSuffix(SuffixEntry(scope, "improtocol" + key, Translator.IOS_ContactEditor_Protocol, im.Protocol, value => im.Protocol = value));
                row.AddSuffix(RemoveButton(scope, "removeim" + key, () => self.Value?.ViewModel.RemoveImAddressCommand.Execute(im)));
                return row;
            });
        }
    }

    private static string Key(int index) => index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
