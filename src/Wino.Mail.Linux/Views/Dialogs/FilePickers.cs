namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// File and folder pickers over <see cref="Gtk.FileDialog"/>, which uses the FileChooser portal when there is one.
/// A portal path (under <c>/run/user/…/doc/</c>) is used as returned: never rebuild a path from its folder and name.
/// Main thread only.
/// </summary>
internal static class FilePickers
{
    /// <summary>Paths of the picked files; empty when dismissed. <paramref name="extensions"/> as <see cref="Extensions"/> returns them.</summary>
    public static async Task<string[]> OpenAsync(Gtk.Window owner, bool multiple, string[] extensions)
    {
        var dialog = Create(extensions);
        try
        {
            if (!multiple) return Path(await dialog.OpenAsync(owner)) is { } path ? [path] : [];

            var model = await dialog.OpenMultipleAsync(owner);
            if (model is null) return [];
            var paths = new List<string>();
            for (uint index = 0; index < model.GetNItems(); index++)
            {
                // The items are private native types of the portal, so they are read through the pointer form.
                if (Path(Gio.FileHelper.NewFromPointer(model.GetItem(index), true)) is { } path) paths.Add(path);
            }
            return [.. paths];
        }
        catch (GLib.GException)
        {
            return [];
        }
    }

    /// <summary>The path to write to; null when dismissed.</summary>
    public static async Task<string?> SaveAsync(Gtk.Window owner, string suggestedName)
    {
        var extension = System.IO.Path.GetExtension(suggestedName).TrimStart('.');
        // A filter with the suggested extension keeps it when the user retypes the name.
        var dialog = Create(extension.Length > 0 ? [extension.ToLowerInvariant()] : []);
        if (!string.IsNullOrEmpty(suggestedName)) dialog.SetInitialName(suggestedName);
        try { return Path(await dialog.SaveAsync(owner)); }
        catch (GLib.GException) { return null; }
    }

    /// <summary>The folder path; null when dismissed.</summary>
    public static async Task<string?> SelectFolderAsync(Gtk.Window owner)
    {
        var dialog = Create([]);
        try { return Path(await dialog.SelectFolderAsync(owner)); }
        catch (GLib.GException) { return null; }
    }

    /// <summary>The extensions in the filters shared callers pass (".png", "png", "*"), without dots, wildcards and duplicates.</summary>
    public static string[] Extensions(object[] typeFilters)
        => [.. typeFilters.OfType<string>()
            .Select(static filter => filter.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(static extension => extension.Length > 0 && extension != "*")
            .Distinct(StringComparer.Ordinal)];

    private static Gtk.FileDialog Create(string[] extensions)
    {
        var dialog = Gtk.FileDialog.New();
        dialog.SetModal(true);
        if (extensions.Length == 0) return dialog;

        var filter = Gtk.FileFilter.New();
        filter.SetName(string.Join(", ", extensions.Select(static extension => "*." + extension)));
        foreach (var extension in extensions) filter.AddSuffix(extension);
        var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
        filters.Append(filter);
        dialog.SetFilters(filters);
        dialog.SetDefaultFilter(filter);
        return dialog;
    }

    private static string? Path(Gio.File? file)
    {
        if (file is null) return null;
        var path = file.GetPath();
        if (path is null) Serilog.Log.Warning("A picked location is not a local file and was skipped.");
        return path;
    }
}
