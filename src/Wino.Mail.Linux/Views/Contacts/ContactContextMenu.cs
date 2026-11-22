using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>One entry of the contact menu; <see cref="Checked"/> makes it a check item, <see cref="Children"/> a submenu.</summary>
internal sealed record ContactMenuEntry(string Title, Action? Run, bool IsEnabled = true, bool? Checked = null,
    WinoIconGlyph Glyph = WinoIconGlyph.None, bool IsDestructive = false, ContactMenuEntry[]? Children = null, bool IsSeparator = false)
{
    public static ContactMenuEntry Separator { get; } = new(string.Empty, null, IsSeparator: true);
}

/// <summary>
/// The contact row menu (Windows ContactCardMenuFlyout, macOS PopulateContextMenu) as data, built for the contacts it
/// acts on: Send mail, Edit, Favourite, Add to list, Category, Remove from list, Delete. The category check has two
/// states; a category that only some targets carry shows unchecked.
/// </summary>
internal static class ContactContextMenu
{
    public sealed record Actions(
        Action<AccountContactViewModel[]> SendMail,
        Action<AccountContactViewModel> Edit,
        Action<AccountContactViewModel[]> ToggleFavorite,
        Action<ContactList, AccountContactViewModel[]> AddToList,
        Action<MailCategory, AccountContactViewModel[]> ToggleCategory,
        Action<AccountContactViewModel[]> RemoveFromList,
        Action<AccountContactViewModel[]> Delete);

    public static ContactMenuEntry[] Build(AccountContactViewModel clicked, AccountContactViewModel[] targets, ContactList[] assignableLists,
        bool hasListCandidates, MailCategory[] categories, bool isListFilter, Actions actions)
    {
        var entries = new List<ContactMenuEntry>
        {
            new(Translator.ContactAction_SendMail, () => actions.SendMail(targets), targets.Any(static t => t.CanSendMail), Glyph: WinoIconGlyph.Send),
        };
        if (targets.Length == 1)
            entries.Add(new(Translator.ContactAction_Edit + "…", () => actions.Edit(clicked), clicked.CanEdit, Glyph: WinoIconGlyph.Rename));
        var anyNotFavorite = targets.Any(static t => !t.IsFavorite);
        entries.Add(new(anyNotFavorite ? Translator.ContactAction_Favorite : Translator.ContactAction_Unfavorite, () => actions.ToggleFavorite(targets),
            Glyph: WinoIconGlyph.Star));
        if (hasListCandidates)
        {
            ContactMenuEntry[] lists = assignableLists.Length == 0
                ? [new(Translator.ContactsShellMac_NoAssignableLists, null, false)]
                : [.. assignableLists.Select(list => new ContactMenuEntry(list.Name ?? string.Empty, () => actions.AddToList(list, targets)))];
            entries.Add(new(Translator.ContactAction_AddToList, null, Glyph: WinoIconGlyph.People, Children: lists));
        }
        if (categories.Length > 0)
        {
            ContactMenuEntry[] items = [.. categories.Select(category => new ContactMenuEntry(category.Name ?? string.Empty,
                () => actions.ToggleCategory(category, targets),
                Checked: targets.All(t => t.SourceContact?.Categories?.Any(c => c.Id == category.Id) == true)))];
            entries.Add(new(Translator.MailCategoryMenuItem, null, Glyph: WinoIconGlyph.SpecialFolderCategory, Children: items));
        }
        if (isListFilter)
            entries.Add(new(Translator.ContactAction_RemoveFromList, () => actions.RemoveFromList(targets), Glyph: WinoIconGlyph.List));
        entries.Add(ContactMenuEntry.Separator);
        entries.Add(new(Translator.ContactAction_Delete, () => actions.Delete(targets), targets.Any(static t => t.CanDelete),
            Glyph: WinoIconGlyph.Delete, IsDestructive: true));
        return [.. entries];
    }

    public static void Populate(WinoMenuBuilder builder, ContactMenuEntry[] entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator) builder.Separator();
            else if (entry.Children is { } children)
                builder.Submenu(entry.Title, sub => Populate(sub, children), entry.IsEnabled && children.Length > 0, entry.Glyph);
            else if (entry.Checked is { } isChecked)
                builder.Check(entry.Title, isChecked, _ => entry.Run?.Invoke(), entry.IsEnabled);
            else
                builder.Item(entry.Title, () => entry.Run?.Invoke(), entry.IsEnabled && entry.Run is not null, icon: entry.Glyph, destructive: entry.IsDestructive);
        }
    }

    /// <summary>Finds an entry by its title path ("Add to list/CT list").</summary>
    public static ContactMenuEntry? Find(ContactMenuEntry[] entries, string path)
    {
        var parts = path.Split('/');
        ContactMenuEntry? found = null;
        var level = entries;
        foreach (var part in parts)
        {
            found = level.FirstOrDefault(entry => !entry.IsSeparator && string.Equals(entry.Title, part, StringComparison.Ordinal));
            if (found is null) return null;
            level = found.Children ?? [];
        }
        return found;
    }

    public static string Describe(ContactMenuEntry[] entries)
        => string.Join("; ", entries.Where(static e => !e.IsSeparator).Select(e =>
            e.Title + (e.IsEnabled ? string.Empty : " (disabled)") + (e.Checked is { } c ? (c ? " [x]" : " [ ]") : string.Empty)
            + (e.IsDestructive ? " (red)" : string.Empty) + (e.Children is { } kids ? " {" + Describe(kids) + "}" : string.Empty)));
}
