using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.Controls.Linux.Compose;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

internal sealed partial class ComposeView
{
    private readonly RecipientBoxes _recipients = new();
    private readonly List<Task> _pendingRecipients = [];

    private Gtk.Widget ToField => _recipients.To.Root;
    private Gtk.Widget CcField => _recipients.Cc.Root;
    private Gtk.Widget BccField => _recipients.Bcc.Root;
    internal WinoRecipientTokenBox ToBox => _recipients.To;
    internal WinoRecipientTokenBox CcBox => _recipients.Cc;
    internal WinoRecipientTokenBox BccBox => _recipients.Bcc;

    private sealed class RecipientBoxes
    {
        public RecipientBoxes()
        {
            var group = "wino-recipients:" + Guid.NewGuid().ToString("N");
            To = new WinoRecipientTokenBox("to", Translator.ComposerTo.Trim().TrimEnd(':'), group);
            Cc = new WinoRecipientTokenBox("cc", "Cc", group);
            Bcc = new WinoRecipientTokenBox("bcc", "Bcc", group);
        }

        public WinoRecipientTokenBox To { get; }
        public WinoRecipientTokenBox Cc { get; }
        public WinoRecipientTokenBox Bcc { get; }
        public WinoRecipientTokenBox[] All => [To, Cc, Bcc];
    }

    partial void InitializeRecipients()
    {
        foreach (var box in _recipients.All)
        {
            var collection = CollectionFor(box);
            box.Parse = static text => ComposeRecipientRules.TryParseTyped(text, out var address, out var name) ? new RecipientToken(address, name) : null;
            box.Validate = (address, _) =>
            {
                if (!ComposePageViewModel.ContainsAddress(collection, address)) return true;
                ViewModel.NotifyAddressExists();
                return false;
            };
            box.Refused += (_, text) => ViewModel.NotifyInvalidEmail(text);
            box.Committed += (sender, address, name) => TrackRecipient(AddRecipientAsync(CollectionFor(sender), address, name));
            box.Removed += (sender, address) => RemoveRecipient(CollectionFor(sender), address);
            box.MoveRequested += (sender, address, target) => MoveRecipient(sender, BoxFor(target), address);
            box.DroppedFrom += (target, address, _, source) => MoveRecipient(BoxFor(source), target, address);
            box.FocusLeft += static sender =>
            {
                if (ComposeRecipientRules.IsCompleteAddress(sender.TypedText)) sender.CommitTyped();
            };
            box.ChipMenuRequested += ShowChipMenu;
            Bindings.Own(box);
            ObserveRecipients(collection, box);
        }
    }

    private void FocusRecipients() => _recipients.To.FocusEntry();

    private ObservableCollection<AccountContact> CollectionFor(WinoRecipientTokenBox box)
        => ReferenceEquals(box, _recipients.Cc) ? ViewModel.CCItems : ReferenceEquals(box, _recipients.Bcc) ? ViewModel.BCCItems : ViewModel.ToItems;

    private WinoRecipientTokenBox BoxFor(string id) => id switch { "cc" => _recipients.Cc, "bcc" => _recipients.Bcc, _ => _recipients.To };

    private void ObserveRecipients(ObservableCollection<AccountContact> collection, WinoRecipientTokenBox box)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => RunOnUI(() => WriteRecipients(box, collection));
        collection.CollectionChanged += handler;
        Bindings.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
        WriteRecipients(box, collection);
    }

    private static void WriteRecipients(WinoRecipientTokenBox box, IEnumerable<AccountContact> contacts)
        => box.SetRecipients([.. contacts.Where(static c => !string.IsNullOrWhiteSpace(c.Address)).Select(static c => new RecipientToken(c.Address.Trim(), c.Name))]);

    private void TrackRecipient(Task add)
    {
        _pendingRecipients.Add(add);
        Observe(add);
        ScheduleAutosave();
    }

    private async Task AddRecipientAsync(ObservableCollection<AccountContact> collection, string address, string? name)
    {
        var recipient = await ViewModel.GetAddressInformationAsync(address, collection);
        if (recipient is null) return;
        if (string.IsNullOrWhiteSpace(recipient.Name) && name is not null) recipient.Name = name;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (!IsReleased) ViewModel.TryAddRecipient(collection, recipient);
        });
    }

    private void RemoveRecipient(ObservableCollection<AccountContact> collection, string address)
    {
        foreach (var contact in collection.ToArray())
            if (string.Equals(contact.Address?.Trim(), address, StringComparison.OrdinalIgnoreCase)) collection.Remove(contact);
        ScheduleAutosave();
    }

    private void MoveRecipient(WinoRecipientTokenBox source, WinoRecipientTokenBox target, string address)
    {
        if (ReferenceEquals(source, target)) return;
        var from = CollectionFor(source);
        var to = CollectionFor(target);
        var contact = from.FirstOrDefault(item => string.Equals(item.Address?.Trim(), address, StringComparison.OrdinalIgnoreCase));
        if (contact is null) return;
        if (!ReferenceEquals(target, _recipients.To)) ViewModel.IsCCBCCVisible = true;
        from.Remove(contact);
        if (!ComposePageViewModel.ContainsAddress(to, address)) to.Add(contact);
        ScheduleAutosave();
    }

    private void ShowChipMenu(WinoRecipientTokenBox box, WinoRecipientChip chip, double? x, double? y)
    {
        var index = Array.IndexOf(box.Addresses, chip.Address);
        if (index < 0) return;
        var menu = WinoMenus.Create()
            .Item(Translator.Buttons_Copy, () => box.RunChipCommand(index, "copy"))
            .Item(Translator.Buttons_Edit, () => box.RunChipCommand(index, "edit"))
            .Separator();
        foreach (var target in new[] { "to", "cc", "bcc" })
        {
            if (target == box.Id) continue;
            var title = target == "to" ? Translator.ComposerTo.Trim().TrimEnd(':') : target == "cc" ? "Cc" : "Bcc";
            menu.Item(string.Format(Translator.DragMoveToFolderCaption, title), () => box.RunChipCommand(index, target));
        }
        menu.Separator().Item(Translator.Buttons_Remove, () => box.RunChipCommand(index, "remove"));
        ContextMenu.Show(chip.Root, menu.Build(), x, y);
    }

    /// <param name="includeTyped">
    /// False for autosave: the address still being typed is not a recipient yet. Send and closing the page take it as typed.
    /// </param>
    internal async Task SyncAllRecipientsAsync(bool includeTyped = true)
    {
        Task[] pending = [];
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (includeTyped && !IsReleased)
                foreach (var box in _recipients.All) box.CommitTyped();
            pending = [.. _pendingRecipients];
            _pendingRecipients.Clear();
        });
        try { await Task.WhenAll(pending); }
        catch (Exception exception) { if (!IsReleased) ReportError(exception); }
    }

#if DEBUG
    internal async Task DebugSetRecipientsAsync(string field, string text)
    {
        var box = BoxFor(field);
        if (!ReferenceEquals(box, _recipients.To)) ViewModel.IsCCBCCVisible = true;
        box.Entry.SetText(text);
        box.CommitTyped();
        await SyncAllRecipientsAsync(false);
    }
#endif
}
