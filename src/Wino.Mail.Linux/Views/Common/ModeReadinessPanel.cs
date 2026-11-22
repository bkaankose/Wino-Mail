using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Bindings;

namespace Wino.Mail.Linux.Views.Common;

/// <summary>
/// The blocked state of a mode (Windows ModeReadinessPanel): title, message, spinner, the primary action and Manage
/// accounts. It covers the page while <see cref="ModeReadinessViewModel.IsReady"/> is false.
/// </summary>
internal sealed class ModeReadinessPanel : IDisposable
{
    private readonly Adw.StatusPage _page = Adw.StatusPage.New();
    private readonly Adw.Spinner _spinner = Adw.Spinner.New();
    private readonly Gtk.Button _primary = Gtk.Button.New();
    private readonly Gtk.Button _secondary = Gtk.Button.New();
    private readonly BindingScope _scope = new();

    public ModeReadinessPanel(ModeReadinessViewModel readiness, WinoIconGlyph glyph, IDispatcher dispatcher, Action<Exception> error)
    {
        _page.AddCssClass("background");
        _page.SetVexpand(true);
        _page.SetHexpand(true);
        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        column.SetHalign(Gtk.Align.Center);
        var icon = new WinoIcon(glyph, 56).Root;
        icon.AddCssClass("dim-label");
        column.Append(icon);
        _spinner.SetSizeRequest(32, 32);
        _spinner.SetVisible(false);
        column.Append(_spinner);
        _primary.AddCssClass("suggested-action");
        _primary.AddCssClass("pill");
        _secondary.AddCssClass("pill");
        _secondary.SetLabel(Translator.MailEmptyState_ManageAccounts);
        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttons.SetHalign(Gtk.Align.Center);
        buttons.Append(_primary);
        buttons.Append(_secondary);
        column.Append(buttons);
        _page.SetChild(column);
        _page.SetVisible(false);

        var bind = new GtkBinder<ModeReadinessViewModel>(readiness, _scope, dispatcher, error);
        bind.BindCommand(_primary, readiness.PrimaryActionCommand);
        bind.BindCommand(_secondary, readiness.ManageAccountsCommand);
        // The ViewModel raises several names per change; every value is re-read on each one.
        bind.BindAny(static r => (r.IsReady, r.Title, r.Message, r.IsProgressVisible, r.PrimaryActionText, r.IsPrimaryActionVisible, r.IsSecondaryActionVisible), Apply);
    }

    public Gtk.Widget Root => _page;

    public bool IsShown => _page.GetVisible();

    public void Dispose() => _scope.Dispose();

    private void Apply((bool IsReady, string? Title, string? Message, bool Progress, string? Primary, bool PrimaryVisible, bool SecondaryVisible) state)
    {
        _page.SetVisible(!state.IsReady);
        _page.SetTitle(GLib.Functions.MarkupEscapeText(state.Title ?? string.Empty, -1));
        _page.SetDescription(GLib.Functions.MarkupEscapeText(state.Message ?? string.Empty, -1));
        _spinner.SetVisible(state.Progress);
        _primary.SetLabel(state.Primary ?? string.Empty);
        _primary.SetVisible(state.PrimaryVisible && !string.IsNullOrEmpty(state.Primary));
        _secondary.SetVisible(state.SecondaryVisible);
    }
}
