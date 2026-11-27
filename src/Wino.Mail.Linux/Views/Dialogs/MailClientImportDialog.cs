using System.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// Import from other mail clients (Windows MailClientImportDialog): the sign-in notice, the scan with the client being
/// read, then one row per found account with a check box. Accounts Wino already has are shown but cannot be picked.
/// The scan starts when the dialog opens and stops when it closes.
/// </summary>
internal static class MailClientImportDialog
{
#if DEBUG
    private static FormDialog? _open;
    private static readonly List<(MailClientImportItem Item, Gtk.CheckButton Check)> _openRows = [];
#endif

    /// <summary>Main thread only. The picked accounts, or null when the dialog was cancelled.</summary>
    public static async Task<List<MailClientImportCandidate>?> PresentAsync(Gtk.Widget owner, MailClientImportDialogModel model, IDispatcher dispatcher)
    {
        var form = new FormDialog(Translator.MailClientImport_Title, null, Translator.Buttons_Import)
        {
            ContentWidth = 480,
            CanConfirm = () => model.CanImport,
        };

        var notice = new WinoInfoBar(WinoInfoBarSeverity.Informational, null, Translator.MailClientImport_SignInNotice) { IsClosable = false };
        form.Own(notice);
        form.Add(notice.Root);

        var scanStatus = Gtk.Label.New(string.Empty);
        scanStatus.SetXalign(0);
        scanStatus.SetWrap(true);
        var scanning = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        var spinner = Adw.Spinner.New();
        spinner.SetSizeRequest(20, 20);
        scanning.Append(spinner);
        scanning.Append(scanStatus);
        form.Add(scanning);

        var noAccounts = Gtk.Label.New(Translator.MailClientImport_NoAccounts);
        noAccounts.SetXalign(0);
        var error = Gtk.Label.New(string.Empty);
        error.SetXalign(0);
        error.SetWrap(true);
        error.AddCssClass("error");
        var empty = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        empty.Append(noAccounts);
        empty.Append(error);
        form.Add(empty);

        var list = Gtk.ListBox.New();
        list.SetSelectionMode(Gtk.SelectionMode.None);
        list.AddCssClass("boxed-list");
        WinoA11y.Label(list, Translator.MailClientImport_Title);
        form.Add(list);

        var rowsBuilt = false;
        void Refresh()
        {
            scanning.SetVisible(model.IsScanning);
            scanStatus.SetText(model.ScanStatus);
            empty.SetVisible(model.IsNoAccountsMessageVisible);
            error.SetText(model.ErrorMessage);
            error.SetVisible(!string.IsNullOrEmpty(model.ErrorMessage));
            list.SetVisible(model.IsAccountListVisible);
            form.PrimaryVisible = !model.IsNoAccountsMessageVisible;
            // Items change only while the scan runs; the rows are built once it completed.
            if (model.IsScanCompleted && !rowsBuilt)
            {
                rowsBuilt = true;
                foreach (var item in model.Items) list.Append(Row(item, form));
            }
            form.Refresh();
        }

        var coalescer = new MainLoop.Coalescer(Refresh);
        form.Own(coalescer);
        PropertyChangedEventHandler changed = (_, _) => _ = dispatcher.ExecuteOnUIThread(coalescer.Request);
        model.PropertyChanged += changed;
        form.Own(new ActionDisposable(() => model.PropertyChanged -= changed));
        Refresh();

        using var cancellation = new CancellationTokenSource();
#if DEBUG
        _open = form;
#endif
        try
        {
            var result = form.PresentAsync(owner);
            Observe(model.ScanAsync(cancellation.Token));
            return await result ? model.GetSelectedCandidates() : null;
        }
        finally
        {
            cancellation.Cancel();
#if DEBUG
            if (ReferenceEquals(_open, form))
            {
                _open = null;
                _openRows.Clear();
            }
#endif
        }
    }

    private static Adw.ActionRow Row(MailClientImportItem item, FormDialog form)
    {
        var row = Adw.ActionRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(item.Title);
        row.SetSubtitle(item.Subtitle);
        row.SetTitleLines(1);
        row.SetSubtitleLines(1);

        // A row lays out its prefixes last added first: the check box leads and the provider icon follows it.
        var icon = new WinoIcon(item.IconGlyph, 24).Root;
        icon.SetValign(Gtk.Align.Center);
        row.AddPrefix(icon);

        var check = Gtk.CheckButton.New();
        check.SetValign(Gtk.Align.Center);
        check.SetActive(item.IsSelected && item.CanSelect);
        WinoA11y.Label(check, item.Title);
        row.AddPrefix(check);
#if DEBUG
        _openRows.Add((item, check));
#endif

        if (item.CanSelect)
        {
            row.SetActivatableWidget(check);
            form.Own(GtkSignals.OnToggled(check, () => item.IsSelected = check.GetActive()));
        }
        else
        {
            row.SetSensitive(false);
        }
        return row;
    }

#if DEBUG
    internal static string DebugDescribe()
    {
        if (_open is null) return "no import dialog open\n";
        var text = new System.Text.StringBuilder();
        foreach (var (item, check) in _openRows)
            text.Append($"{item.Title} | {item.Subtitle} | checked={check.GetActive()} enabled={check.IsSensitive()} glyph={item.IconGlyph}\n");
        return text.ToString();
    }

    /// <summary>Clicks a row's check box: AT-SPI offers no action on a GTK 4 check button.</summary>
    internal static bool DebugToggle(string title)
    {
        foreach (var (item, check) in _openRows)
        {
            if (item.Title != title || !check.IsSensitive()) continue;
            check.SetActive(!check.GetActive());
            return true;
        }
        return false;
    }
#endif

    private static async void Observe(Task scan)
    {
        try { await scan; }
        catch (Exception exception) { Serilog.Log.Error(exception, "The mail client scan failed."); }
    }
}
