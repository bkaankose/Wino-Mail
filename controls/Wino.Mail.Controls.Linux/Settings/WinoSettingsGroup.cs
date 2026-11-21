using System.Text;

namespace Wino.Mail.Controls.Linux.Settings;

/// <summary>
/// A boxed group of settings rows (<c>Adw.PreferencesGroup</c>). A null row is a setting this platform does not offer and
/// is skipped; the group hides itself while it is empty unless <see cref="HidesWhenEmpty"/> is false.
/// </summary>
public sealed class WinoSettingsGroup
{
    private readonly List<Gtk.Widget> _rows = [];
    private Gtk.Widget? _headerSuffix;
    private bool _hidesWhenEmpty = true;

    public WinoSettingsGroup(string? title = null, string? description = null)
    {
        Root = Adw.PreferencesGroup.New();
        Title = title;
        Description = description;
        UpdateVisibility();
    }

    public Adw.PreferencesGroup Root { get; }

    // The group's title and description labels are Pango markup.
    public string? Title { set => Root.SetTitle(Escape(value)); }

    public string? Description { set => Root.SetDescription(Escape(value)); }

    public Gtk.Widget? HeaderSuffix
    {
        set
        {
            if (ReferenceEquals(_headerSuffix, value)) return;
            _headerSuffix = value;
            value?.SetValign(Gtk.Align.Center);
            Root.SetHeaderSuffix(value);
        }
    }

    public int Count => _rows.Count;

    public bool HidesWhenEmpty
    {
        get => _hidesWhenEmpty;
        set
        {
            _hidesWhenEmpty = value;
            UpdateVisibility();
        }
    }

    public T? Add<T>(T? row) where T : Gtk.Widget
    {
        if (row is null) return null;
#if DEBUG
        if (row is not Adw.PreferencesRow) throw new ArgumentException("Wrap free-form content in WinoSettingsRows.Custom; a plain widget lands outside the boxed list.", nameof(row));
#endif
        Root.Add(row);
        _rows.Add(row);
        UpdateVisibility();
        return row;
    }

    public void AddRange(params Gtk.Widget?[] rows)
    {
        foreach (var row in rows) Add(row);
    }

    public void Remove(Gtk.Widget row)
    {
        if (!_rows.Remove(row)) return;
        Root.Remove(row);
        UpdateVisibility();
    }

    public void Clear()
    {
        foreach (var row in _rows) Root.Remove(row);
        _rows.Clear();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (_hidesWhenEmpty) Root.SetVisible(_rows.Count > 0);
    }

    private static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                _ => character.ToString(),
            });
        }
        return builder.ToString();
    }
}
