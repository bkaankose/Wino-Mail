using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Bindings;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// The state shown instead of the grid while Calendar cannot be used (Windows ModeReadinessPanel): title, message,
/// progress, and the panel's primary and Manage accounts buttons.
/// </summary>
internal sealed class CalendarReadinessView
{
    private readonly Adw.StatusPage _page = Adw.StatusPage.New();
    private readonly Adw.Spinner _spinner = Adw.Spinner.New();
    private readonly Gtk.Button _primary = Gtk.Button.New();
    private readonly Gtk.Button _secondary = Gtk.Button.New();

    public CalendarReadinessView()
    {
        _page.AddCssClass("compact");
        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        column.SetHalign(Gtk.Align.Center);
        var icon = new WinoIcon(WinoIconGlyph.Calendar, 40).Root;
        icon.AddCssClass("dim-label");
        column.Append(icon);
        _spinner.SetVisible(false);
        column.Append(_spinner);
        _primary.AddCssClass("suggested-action");
        _primary.AddCssClass("pill");
        _secondary.AddCssClass("pill");
        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttons.SetHalign(Gtk.Align.Center);
        buttons.Append(_primary);
        buttons.Append(_secondary);
        column.Append(buttons);
        _page.SetChild(column);
    }

    public Gtk.Widget Root => _page;

    public void Bind(GtkBinder<ModeReadinessViewModel> bind)
    {
        bind.BindCommand(_primary, bind.Source.PrimaryActionCommand);
        bind.BindCommand(_secondary, bind.Source.ManageAccountsCommand);
        bind.BindAny(static r => (r.PrimaryActionText, r.IsPrimaryActionVisible, r.SecondaryActionText, r.IsSecondaryActionVisible), state =>
        {
            _primary.SetLabel(state.PrimaryActionText ?? string.Empty);
            _primary.SetVisible(state.IsPrimaryActionVisible);
            _secondary.SetLabel(state.SecondaryActionText ?? string.Empty);
            _secondary.SetVisible(state.IsSecondaryActionVisible);
        });
    }

    public void Update(string? title, string? message, bool progress)
    {
        _page.SetTitle(title ?? string.Empty);
        _page.SetDescription(GLib.Functions.MarkupEscapeText(message ?? string.Empty, -1));
        _spinner.SetVisible(progress);
    }
}
