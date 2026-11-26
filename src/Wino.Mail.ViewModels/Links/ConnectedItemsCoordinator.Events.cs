#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Links;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.ViewModels.Links;

public sealed partial class ConnectedItemsCoordinator
{
    /// <summary>
    /// Opens the event composer prefilled from the mail: its subject, a short excerpt, and the sender and the To and
    /// Cc recipients minus the user's own addresses as attendees. The link is stored once the server confirms the event.
    /// </summary>
    /// <summary>
    /// Opens a new event linked to <paramref name="mail"/>. A time, title or place found in the message (a booking, a
    /// date in the text) prefill it; otherwise it starts at the next full hour with the subject.
    /// </summary>
    public async Task CreateEventFromMailAsync(MailCopy mail, DateTime? start = null, DateTime? end = null, bool isAllDay = false,
                                               string? title = null, string? location = null)
    {
        ArgumentNullException.ThrowIfNull(mail);

        var accountId = GetMailAccountId(mail);
        var account = mail.AssignedAccount ?? await accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        var ownAddresses = await GetOwnAddressesAsync(account).ConfigureAwait(false);
        var attendees = await GetMailParticipantsAsync(mail, accountId, ownAddresses).ConfigureAwait(false);
        var calendar = await GetDefaultWritableCalendarAsync(accountId).ConfigureAwait(false);
        title = !string.IsNullOrWhiteSpace(title) ? title.Trim()
            : string.IsNullOrWhiteSpace(mail.Subject) ? Translator.MailItemNoSubject : mail.Subject.Trim();
        var startTime = start ?? NextFullHour(DateTime.Now);
        var endTime = end is { } endValue && endValue > startTime ? endValue : isAllDay ? startTime.Date.AddDays(1) : startTime.AddHours(1);

        var args = new CalendarEventComposeNavigationArgs
        {
            SelectedCalendarId = calendar?.Id,
            RequireCalendarPickerWhenUnresolved = calendar is null,
            AccountAddressHints = account?.Address is { Length: > 0 } address ? [address] : [],
            Title = title,
            Location = location?.Trim() ?? string.Empty,
            IsAllDay = isAllDay,
            StartDate = startTime,
            EndDate = endTime,
            NotesHtml = ToHtmlParagraph(Snippet(mail.PreviewText)),
            Attendees = attendees,
            PendingLinks =
            [
                new ItemLink
                {
                    SourceKind = ItemLinkKind.Mail,
                    SourceAccountId = accountId,
                    SourceKey = ItemLinkKeys.ForMail(mail),
                    SourceTitle = title,
                    SourceSubtitle = string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress ?? string.Empty : mail.FromName,
                    TargetKind = ItemLinkKind.Event,
                    TargetAccountId = calendar?.AccountId ?? accountId,
                    TargetTitle = title,
                    Relation = ItemLinkRelation.CreatedFrom
                }
            ]
        };

        await OpenEventComposerAsync(args).ConfigureAwait(false);
    }

    private Task OpenEventComposerAsync(CalendarEventComposeNavigationArgs args)
        => dispatcher.ExecuteOnUIThread(() =>
            navigationService.ChangeApplicationMode(WinoApplicationMode.Calendar, new ShellModeActivationContext { Parameter = args }));

    private async Task<AccountCalendar?> GetDefaultWritableCalendarAsync(Guid accountId)
    {
        var calendars = await calendarService.GetAccountCalendarsAsync(accountId).ConfigureAwait(false) ?? [];
        return calendars
            .Where(static calendar => !calendar.IsReadOnly)
            .OrderByDescending(static calendar => calendar.IsPrimary)
            .FirstOrDefault();
    }

    private async Task<HashSet<string>> GetOwnAddressesAsync(MailAccount? account)
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (account is null)
            return addresses;

        if (!string.IsNullOrWhiteSpace(account.Address))
            addresses.Add(account.Address.Trim());

        try
        {
            foreach (var alias in await accountService.GetAccountAliasesAsync(account.Id).ConfigureAwait(false) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(alias.AliasAddress))
                    addresses.Add(alias.AliasAddress.Trim());
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not read the aliases of account {AccountId}.", account.Id);
        }

        return addresses;
    }

    /// <summary>The sender, then To and Cc from the stored MIME. Without the MIME only the sender is known.</summary>
    private async Task<List<CalendarEventAttendeeDraft>> GetMailParticipantsAsync(MailCopy mail, Guid accountId, HashSet<string> ownAddresses)
    {
        var participants = new List<CalendarEventAttendeeDraft>();
        var seen = new HashSet<string>(ownAddresses, StringComparer.OrdinalIgnoreCase);

        void Add(string? name, string? address)
        {
            var email = address?.Trim();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || !seen.Add(email))
                return;

            participants.Add(new CalendarEventAttendeeDraft(string.IsNullOrWhiteSpace(name) ? email : name.Trim(), email));
        }

        Add(mail.FromName, mail.FromAddress);

        try
        {
            if (accountId != Guid.Empty && await mimeFileService.IsMimeExistAsync(accountId, mail.FileId).ConfigureAwait(false) &&
                await mimeFileService.GetMimeMessageInformationAsync(mail.FileId, accountId).ConfigureAwait(false) is { MimeMessage: { } message })
            {
                foreach (var mailbox in message.To.Mailboxes.Concat(message.Cc.Mailboxes))
                    Add(mailbox.Name, mailbox.Address);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not read the recipients of a mail for a new event.");
        }

        return participants;
    }

    private static DateTime NextFullHour(DateTime now)
        => new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind).AddHours(1);

    private static string ToHtmlParagraph(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : $"<p>{WebUtility.HtmlEncode(text).Replace("\r\n", "<br/>").Replace("\n", "<br/>")}</p>";
}
