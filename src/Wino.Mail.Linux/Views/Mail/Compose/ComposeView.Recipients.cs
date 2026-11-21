using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

internal sealed partial class ComposeView
{
    private static readonly char[] RecipientSeparators = [',', ';', '\n'];
    private readonly Gtk.Entry _to = RecipientEntry(Translator.ComposerTo.Trim().TrimEnd(':'));
    private readonly Gtk.Entry _cc = RecipientEntry("Cc");
    private readonly Gtk.Entry _bcc = RecipientEntry("Bcc");
    private bool _syncingRecipients;

    private Gtk.Widget ToField => _to;
    private Gtk.Widget CcField => _cc;
    private Gtk.Widget BccField => _bcc;

    private static Gtk.Entry RecipientEntry(string label)
    {
        var entry = Gtk.Entry.New();
        entry.SetHasFrame(false);
        entry.SetHexpand(true);
        WinoA11y.Label(entry, label);
        return entry;
    }

    partial void InitializeRecipients()
    {
        ObserveRecipients(ViewModel.ToItems, _to);
        ObserveRecipients(ViewModel.CCItems, _cc);
        ObserveRecipients(ViewModel.BCCItems, _bcc);
        foreach (var entry in new[] { _to, _cc, _bcc })
            Bindings.Own(GtkSignals.Connect<Gtk.Entry>(h => entry.OnActivate += h, h => entry.OnActivate -= h, (_, _) => Observe(SyncRecipientsAsync(entry, true))));
    }

    private void FocusRecipients() => _to.GrabFocus();

    private ObservableCollection<AccountContact> CollectionFor(Gtk.Entry entry)
        => ReferenceEquals(entry, _cc) ? ViewModel.CCItems : ReferenceEquals(entry, _bcc) ? ViewModel.BCCItems : ViewModel.ToItems;

    private void ObserveRecipients(ObservableCollection<AccountContact> collection, Gtk.Entry entry)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => RunOnUI(() => { if (!_syncingRecipients) WriteRecipients(entry, collection); });
        collection.CollectionChanged += handler;
        Bindings.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
        WriteRecipients(entry, collection);
    }

    private static void WriteRecipients(Gtk.Entry entry, IEnumerable<AccountContact> contacts)
    {
        var text = string.Join(", ", contacts.Where(static c => !string.IsNullOrWhiteSpace(c.Address)).Select(static c => c.Address));
        if (entry.GetText() != text) entry.SetText(text);
    }

    internal async Task SyncAllRecipientsAsync(bool includeTyped = true)
    {
        await SyncRecipientsAsync(_to, includeTyped);
        await SyncRecipientsAsync(_cc, includeTyped);
        await SyncRecipientsAsync(_bcc, includeTyped);
    }

    private async Task SyncRecipientsAsync(Gtk.Entry entry, bool includeTyped)
    {
        string text = string.Empty;
        await Dispatcher.ExecuteOnUIThread(() => text = entry.GetText());
        var collection = CollectionFor(entry);
        var parsed = new List<(string Address, string? Name)>();
        var invalid = new List<string>();
        foreach (var part in text.Split(RecipientSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ComposeRecipientRules.TryParseTyped(part, out var address, out var name) && (includeTyped || ComposeRecipientRules.IsCompleteAddress(part)))
                parsed.Add((address, name));
            else if (includeTyped) invalid.Add(part);
        }

        _syncingRecipients = true;
        try
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                foreach (var contact in collection.ToArray())
                    if (!parsed.Any(item => string.Equals(item.Address, contact.Address?.Trim(), StringComparison.OrdinalIgnoreCase))) collection.Remove(contact);
            });
            foreach (var (address, name) in parsed)
            {
                if (ComposePageViewModel.ContainsAddress(collection, address)) continue;
                var recipient = await ViewModel.GetAddressInformationAsync(address, collection);
                if (recipient is null) continue;
                if (string.IsNullOrWhiteSpace(recipient.Name) && name is not null) recipient.Name = name;
                await Dispatcher.ExecuteOnUIThread(() => ViewModel.TryAddRecipient(collection, recipient));
            }
        }
        finally { _syncingRecipients = false; }

        if (invalid.Count > 0)
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                foreach (var part in invalid) ViewModel.NotifyInvalidEmail(part);
                WriteRecipients(entry, collection);
            });
    }

#if DEBUG
    internal Task DebugSetRecipientsAsync(string field, string text)
    {
        var entry = field switch { "cc" => _cc, "bcc" => _bcc, _ => _to };
        if (!ReferenceEquals(entry, _to)) ViewModel.IsCCBCCVisible = true;
        entry.SetText(text);
        return SyncRecipientsAsync(entry, true);
    }

    internal string DebugRecipients(string field) => (field switch { "cc" => _cc, "bcc" => _bcc, _ => _to }).GetText();
#endif
}
