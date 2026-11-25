using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>A manual alias: the address and an optional reply-to address (Windows CreateAccountAliasDialog).</summary>
internal static class CreateAliasDialog
{
#if DEBUG
    private static (Adw.EntryRow Address, Adw.EntryRow ReplyTo, FormDialog Form)? _open;

    internal static bool DebugFill(string address, string replyTo, bool confirm)
    {
        if (_open is not { } open) return false;
        open.Address.SetText(address);
        open.ReplyTo.SetText(replyTo);
        if (confirm) open.Form.DebugConfirm();
        return true;
    }
#endif

    /// <summary>Main thread only.</summary>
    public static async Task<MailAccountAlias?> PresentAsync(Gtk.Widget owner)
    {
        var form = new FormDialog(Translator.CreateAccountAliasDialog_Title, Translator.CreateAccountAliasDialog_Description, Translator.Buttons_Create);
        var address = Entry(Translator.CreateAccountAliasDialog_AliasAddress, Translator.CreateAccountAliasDialog_AliasAddressPlaceholder);
        var replyTo = Entry(Translator.CreateAccountAliasDialog_ReplyToAddress, Translator.CreateAccountAliasDialog_ReplyToAddressPlaceholder);
        var group = form.AddGroup();
        group.Add(address);
        group.Add(replyTo);
        form.CanConfirm = () => !string.IsNullOrWhiteSpace(address.GetText());
        form.Own(GtkSignals.OnChanged(address, form.Refresh));
        form.SetInitialFocus(address);
#if DEBUG
        _open = (address, replyTo, form);
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
        return new MailAccountAlias
        {
            Id = Guid.NewGuid(),
            AliasAddress = address.GetText().Trim(),
            ReplyToAddress = replyTo.GetText().Trim(),
            IsPrimary = false,
            IsVerified = false,
            Source = AliasSource.Manual,
            SendCapability = AliasSendCapability.Unknown,
        };
    }

    private static Adw.EntryRow Entry(string title, string hint)
    {
        var row = Adw.EntryRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(title);
        row.SetTooltipText(hint);
        row.SetInputPurpose(Gtk.InputPurpose.Email);
        row.SetActivatesDefault(true);
        return row;
    }
}
