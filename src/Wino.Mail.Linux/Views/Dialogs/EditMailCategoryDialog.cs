using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// A category's name and colour pair from the shared palette, with a live chip preview (Windows EditMailCategoryDialog).
/// Empty and duplicate names are reported by the shared <c>CategoryEditor</c> after the dialog returns.
/// </summary>
internal static class EditMailCategoryDialog
{
#if DEBUG
    private static (Adw.EntryRow Name, Gtk.ToggleButton[] Swatches, FormDialog Form, Func<string> Describe)? _open;

    internal static string? DebugDescribeOpen() => _open?.Describe();

    internal static bool DebugSet(string name, int swatch, bool confirm)
    {
        if (_open is not { } open) return false;
        if (name.Length > 0) open.Name.SetText(name);
        if (swatch >= 0 && swatch < open.Swatches.Length) open.Swatches[swatch].SetActive(true);
        if (confirm) open.Form.DebugConfirm();
        return true;
    }
#endif

    /// <summary>Main thread only.</summary>
    public static async Task<MailCategoryDialogResult?> PresentAsync(Gtk.Widget owner, MailCategory? category)
    {
        var options = MailCategoryPalette.DefaultOptions.ToArray();
        var form = new FormDialog(category is null ? Translator.MailCategoryDialog_CreateTitle : Translator.MailCategoryDialog_EditTitle, null, Translator.Buttons_Save);

        var name = Adw.EntryRow.New();
        name.SetUseMarkup(false);
        name.SetTitle(Translator.MailCategoryDialog_Name);
        name.SetTooltipText(Translator.MailCategoryDialog_NamePlaceholder);
        name.SetText(category?.Name ?? string.Empty);
        name.SetActivatesDefault(true);
        form.AddGroup().Add(name);

        var colorLabel = Gtk.Label.New(Translator.MailCategoryDialog_Color);
        colorLabel.AddCssClass("heading");
        colorLabel.SetXalign(0);
        form.Add(colorLabel);

        WinoSwatchCss.EnsureInstalled();
        var wrap = Adw.WrapBox.New();
        wrap.SetChildSpacing(8);
        wrap.SetLineSpacing(8);
        var swatches = new Gtk.ToggleButton[options.Length];
        var selected = Math.Max(0, Array.FindIndex(options, option =>
            string.Equals(option.BackgroundColorHex, category?.BackgroundColorHex, StringComparison.OrdinalIgnoreCase)
            && string.Equals(option.TextColorHex, category?.TextColorHex, StringComparison.OrdinalIgnoreCase)));
        for (var index = 0; index < options.Length; index++)
        {
            var swatch = Gtk.ToggleButton.New();
            swatch.AddCssClass("wino-swatch");
            if (WinoTint.FillClassFor(options[index].BackgroundColorHex, options[index].TextColorHex) is { } fill) swatch.AddCssClass(fill);
            if (index > 0) swatch.SetGroup(swatches[0]);
            WinoA11y.Label(swatch, $"{Translator.MailCategoryDialog_Color} {index + 1}");
            swatch.SetTooltipText($"{options[index].BackgroundColorHex} / {options[index].TextColorHex}");
            swatches[index] = swatch;
            wrap.Append(swatch);
        }
        swatches[selected].SetActive(true);
        form.Add(wrap);

        var preview = Gtk.Label.New(null);
        preview.AddCssClass("wino-chip");
        preview.AddCssClass("wino-category-preview");
        preview.SetHalign(Gtk.Align.Start);
        preview.SetEllipsize(Pango.EllipsizeMode.End);
        preview.SetMaxWidthChars(32);
        form.Add(preview);
        string? previewFill = null;

        int Selected() => Math.Max(0, Array.FindIndex(swatches, swatch => swatch.GetActive()));

        void Update()
        {
            var text = name.GetText().Trim();
            preview.SetText(text.Length > 0 ? text : Translator.MailCategoryDialog_NamePlaceholder);
            var option = options[Selected()];
            var fill = WinoTint.FillClassFor(option.BackgroundColorHex, option.TextColorHex);
            if (previewFill is not null) preview.RemoveCssClass(previewFill);
            if (fill is not null) preview.AddCssClass(fill);
            previewFill = fill;
            form.Refresh();
        }

        form.CanConfirm = () => !string.IsNullOrWhiteSpace(name.GetText());
        form.Own(GtkSignals.OnChanged(name, Update));
        foreach (var swatch in swatches) form.Own(GtkSignals.OnToggled(swatch, Update));
        form.SetInitialFocus(name);
        Update();
#if DEBUG
        _open = (name, swatches, form, () => $"title={form.Dialog.GetTitle()} name={name.GetText()} selected={Selected()} swatches={swatches.Length} preview={preview.GetText()} fill={previewFill}\n");
#endif
        bool confirmed;
        try { confirmed = await form.PresentAsync(owner); }
        finally
        {
#if DEBUG
            _open = null;
#endif
        }
        if (!confirmed) return null;
        var chosen = options[Selected()];
        return new MailCategoryDialogResult(name.GetText().Trim(), chosen.BackgroundColorHex, chosen.TextColorHex);
    }
}
