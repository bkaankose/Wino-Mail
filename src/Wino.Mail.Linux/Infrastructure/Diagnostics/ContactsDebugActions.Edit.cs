#if DEBUG
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.Linux.Views.Contacts;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Editor verbs: edit-new, edit-open (the selected contact), edit-state, edit-fields, edit-tab i, edit-set field value
/// (types into the widget, so the two-way path runs), edit-pick field index (drop-down or combo), edit-add-email,
/// edit-add-phone, edit-add-im, edit-add-child, edit-save, edit-cancel, edit-favorite.
/// </summary>
internal static partial class ContactsDebugActions
{
    internal static ContactEditView Editor
        => ContactEditView.DebugCurrent is { } reference && reference.TryGetTarget(out var view) ? view : throw new InvalidOperationException("no page");

    static partial void AddEditVerbs()
    {
        Verb("edit-new", _ =>
        {
            Services.GetRequiredService<INavigationService>().Navigate(WinoPage.ContactEditPage, new ContactEditNavigationParameter());
            return "navigated";
        });
        Verb("edit-open", _ =>
        {
            var contact = Page.ViewModel.SelectedContact ?? throw new ArgumentException("nothing selected");
            Page.ViewModel.EditContactCommand.Execute(contact);
            return "editing " + contact.Name;
        });
        Verb("edit-state", _ => EditState(Editor));
        Verb("edit-fields", _ => string.Join(", ", Editor.DebugFieldNames.Order(StringComparer.Ordinal)));
        Verb("edit-tab", args =>
        {
            var view = Editor;
            view.Stack.SetVisibleChildName("c" + (args.Length > 0 ? args[0] : "0"));
            return "category=" + view.ViewModel.SelectedCategory;
        });
        Verb("edit-set", args =>
        {
            if (args.Length < 1) throw new ArgumentException("edit-set needs a field");
            var widget = Editor.DebugField(args[0]) ?? throw new ArgumentException("no field " + args[0]);
            var value = string.Join(' ', args[1..]);
            if (widget is Gtk.Editable editable) editable.SetText(value);
            else throw new ArgumentException("not editable");
            return args[0] + "=" + value;
        });
        Verb("edit-pick", args =>
        {
            if (args.Length < 2) throw new ArgumentException("edit-pick needs a field and an index");
            var widget = Editor.DebugField(args[0]) ?? throw new ArgumentException("no field " + args[0]);
            var index = uint.Parse(args[1], CultureInfo.InvariantCulture);
            switch (widget)
            {
                case Adw.ComboRow combo: combo.SetSelected(index); break;
                case Gtk.DropDown dropDown: dropDown.SetSelected(index); break;
                case Adw.SwitchRow toggle: toggle.SetActive(index != 0); break;
                default: throw new ArgumentException("not a choice");
            }
            return args[0] + "=" + index;
        });
        Verb("edit-click", args =>
        {
            var widget = Editor.DebugField(args.Length > 0 ? args[0] : string.Empty) ?? throw new ArgumentException("no field");
            if (widget is Gtk.Button button) button.Activate();
            else if (widget is Gtk.MenuButton menu) menu.Popup();
            return "clicked";
        });
        Verb("edit-scroll", args =>
        {
            var view = Editor;
            var adjustment = view.Scroller.GetVadjustment();
            adjustment.SetValue(args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : 0);
            return $"value={adjustment.GetValue():0} upper={adjustment.GetUpper():0}";
        });
        Verb("edit-import", async args =>
        {
            var path = string.Join(' ', args);
            var draft = await Services.GetRequiredService<IActivationFileImportService>().ImportContactAsync([path]);
            if (draft is null) return "no draft";
            await Services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
                Services.GetRequiredService<INavigationService>().Navigate(WinoPage.ContactEditPage, new ContactEditNavigationParameter(ImportDraft: draft)));
            return $"draft unsupported={draft.HasUnsupportedContent} photo={draft.PhotoBytes?.Length ?? 0}";
        });
        Verb("edit-photo", args =>
        {
            var view = Editor;
            Wino.Mail.Linux.Views.Dialogs.FilePickers.DebugNext.Enqueue(string.Join(' ', args));
            view.ViewModel.ChoosePhotoCommand.ExecuteAsync(null).ContinueWith(static task => Serilog.Log.Error(task.Exception, "Debug photo failed."), TaskContinuationOptions.OnlyOnFaulted);
            return "picking";
        });
        Verb("edit-remove-photo", _ => Run(Editor.ViewModel.RemovePhotoCommand));
        Verb("edit-photo-menu", _ =>
        {
            Editor.ShowPhotoMenu();
            return "shown";
        });
        Verb("edit-notes", args =>
        {
            var widget = Editor.DebugField("notes") as Gtk.TextView ?? throw new ArgumentException("no notes");
            widget.GetBuffer().SetText(string.Join(' ', args).Replace("\\n", "\n", StringComparison.Ordinal), -1);
            return "notes set";
        });
        Verb("edit-add-email", _ => Run(Editor.ViewModel.AddEmailCommand));
        Verb("edit-add-phone", _ => Run(Editor.ViewModel.AddPhoneCommand));
        Verb("edit-add-im", _ => Run(Editor.ViewModel.AddImAddressCommand));
        Verb("edit-add-child", _ => Run(Editor.ViewModel.AddChildCommand));
        Verb("edit-favorite", _ => Run(Editor.ViewModel.ToggleFavoriteCommand));
        Verb("edit-save", _ => Run(Editor.ViewModel.SaveCommand));
        Verb("edit-cancel", _ => Run(Editor.ViewModel.BackCommand));
    }

    private static string Run(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null)) return "cannot";
        command.Execute(null);
        return "ran";
    }

    private static string EditState(ContactEditView view)
    {
        var vm = view.ViewModel;
        var text = new StringBuilder();
        text.Append("edit=").Append(vm.IsEditMode).Append(" dirty=").Append(vm.IsDirty).Append(" saving=").Append(vm.IsSaving)
            .Append(" error=").Append(vm.IsErrorOpen).Append(" '").Append(vm.ErrorMessage).Append('\'')
            .Append(" category=").Append(vm.SelectedCategory)
            .Append(" destination=").Append(vm.SelectedDestination?.DisplayName ?? "none").Append(" destinations=").Append(vm.Destinations.Count)
            .Append(" preview='").Append(vm.PreviewDisplayName).Append("' subtitle='").Append(vm.PreviewSubtitle).Append('\'')
            .Append(" given='").Append(vm.GivenName).Append("' surname='").Append(vm.Surname).Append("' display='").Append(vm.DisplayName)
            .Append("' nickname='").Append(vm.Nickname).Append("' fileas='").Append(vm.FileAs)
            .Append("' company='").Append(vm.CompanyName).Append("' job='").Append(vm.JobTitle).Append("' department='").Append(vm.Department)
            .Append("' office='").Append(vm.OfficeLocation).Append("' manager='").Append(vm.ManagerName).Append("' assistant='").Append(vm.AssistantName)
            .Append("' spouse='").Append(vm.SpouseName).Append("' website='").Append(vm.Website)
            .Append("' birthday=").Append(vm.BirthdayDay).Append('.').Append(vm.BirthdayMonth).Append('.').Append(vm.BirthdayYear)
            .Append(" notes='").Append(vm.Notes?.Replace("\n", "\\n", StringComparison.Ordinal)).Append('\'')
            .Append(" favorite=").Append(vm.IsFavorite)
            .Append(" emails=[").Append(string.Join("; ", vm.EmailAddresses.Select(static e => $"{e.Address}|{e.Label}"))).Append(']')
            .Append(" phones=[").Append(string.Join("; ", vm.PhoneNumbers.Select(static p => $"{p.Number}|{p.Kind}"))).Append(']')
            .Append(" ims=[").Append(string.Join("; ", vm.ImAddresses.Select(static i => $"{i.Address}|{i.Protocol}"))).Append(']')
            .Append(" relations=[").Append(string.Join("; ", vm.Relations.Select(static r => $"{r.Name}|{r.Kind}"))).Append(']')
            .Append(" addresses=[").Append(string.Join("; ", vm.PostalAddresses.Select(static a => $"{a.Kind}:{a.Street},{a.City},{a.Region},{a.PostalCode},{a.Country}"))).Append(']')
            .Append(" lists=[").Append(string.Join("; ", vm.ListMemberships.Select(static l => $"{l.Name}={l.IsMember}"))).Append(']')
            .Append(" categories=[").Append(string.Join("; ", vm.CategoryMemberships.Select(static c => $"{c.Name}={c.IsMember}"))).Append(']')
            .Append(" photo=").Append(vm.PreviewPhotoBytes?.Length ?? 0).Append('/').Append(string.IsNullOrEmpty(vm.PreviewPhotoPath) ? "-" : "path")
            .Append(" canPhoto=").Append(vm.CanEditPhoto)
            .Append(" phoneKinds=").Append(string.Join("|", Enum.GetNames<ContactPhoneKind>()));
        return text.ToString();
    }
}
#endif
