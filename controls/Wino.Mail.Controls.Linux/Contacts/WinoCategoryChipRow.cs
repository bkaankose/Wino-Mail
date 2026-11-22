using Wino.Core.Domain.Entities.Mail;
using Wino.Mail.Controls.Linux.Common;

namespace Wino.Mail.Controls.Linux.Contacts;

/// <summary>
/// The category chips of a contact (Windows ContactCategoryChipTemplate) as <see cref="WinoChip"/>s on the category's
/// colours. Chips whose name and colour did not change are kept, so a rebind of a recycled row allocates nothing.
/// </summary>
public sealed class WinoCategoryChipRow
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
    private readonly List<(WinoChip Chip, string Name, string? Background, string? Foreground)> _shown = [];
    private readonly int _height;

    public WinoCategoryChipRow(int height = 18)
    {
        _height = height;
        _root.SetCanTarget(false);
        _root.SetVisible(false);
    }

    public Gtk.Widget Root => _root;

    public int Count => _shown.Count;

    public void Apply(MailCategory[] categories)
    {
        var keep = 0;
        while (keep < _shown.Count && keep < categories.Length && Same(_shown[keep], categories[keep])) keep++;
        for (var index = _shown.Count - 1; index >= keep; index--)
        {
            _root.Remove(_shown[index].Chip.Root);
            _shown.RemoveAt(index);
        }
        for (var index = keep; index < categories.Length; index++)
        {
            var category = categories[index];
            var chip = new WinoChip(_height) { Text = category.Name ?? string.Empty, MaxWidthChars = 16 };
            chip.SetColors(category.BackgroundColorHex, category.TextColorHex, accent: true);
            _root.Append(chip.Root);
            _shown.Add((chip, category.Name ?? string.Empty, category.BackgroundColorHex, category.TextColorHex));
        }
        _root.SetVisible(_shown.Count > 0);
    }

    private static bool Same((WinoChip Chip, string Name, string? Background, string? Foreground) shown, MailCategory category)
        => string.Equals(shown.Name, category.Name ?? string.Empty, StringComparison.Ordinal)
           && string.Equals(shown.Background, category.BackgroundColorHex, StringComparison.OrdinalIgnoreCase)
           && string.Equals(shown.Foreground, category.TextColorHex, StringComparison.OrdinalIgnoreCase);
}
