#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Voice;
using Wino.Core.Requests;
using Wino.Core.Requests.Contact;
using Wino.Mail.Api.Contracts.Voice;
using Wino.Services.Voice;

namespace Wino.Core.Services.Voice;

/// <summary>
/// Contact steps: the device finds the contacts a plan describes (words of the name or address, a list, a category,
/// favorites) and changes them with the same requests the Contacts page queues. An action meant for one contact that
/// matches several changes nothing unless one name matches exactly.
/// </summary>
public sealed partial class VoicePlanExecutor
{
    private const int ContactPageSize = 200;

    private async Task<StepOutcome> ResolveContactStepAsync(VoicePlanPreviewGroup group, VoicePlanPreviewStep step, ResolveState state, CancellationToken cancellationToken)
    {
        var command = step.Command;
        if (command.UsesResultOf is int source)
        {
            if (!state.ContactTargets.TryGetValue(source - 1, out var found) || found.Count == 0)
                return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_ChainedContactsSkipped, PlainText(step)));

            state.ContactTargets[step.CommandIndex] = found;
            return StepOutcome.Keep(DescribeContactTargets(found, step.Detail));
        }

        var accounts = GroupAccounts(group);
        if (accounts.Count == 0)
            return StepOutcome.Drop(Translator.VoiceIssue_AccountUnnamed);

        var selection = command.Contacts ?? new ContactSelection();
        Guid? accountId = group.Kind == VoicePlanAccountKind.Account ? accounts[0].Id : null;

        Guid? listId = null;
        if (!string.IsNullOrWhiteSpace(selection.ListName))
        {
            var lists = await contactService.GetContactListsAsync().ConfigureAwait(false) ?? [];
            var list = MatchContactList(lists.Where(list => accountId is null || list.MailAccountId is null || list.MailAccountId == accountId), selection.ListName);
            if (list is null)
                return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoContactList, selection.ListName.Trim()));

            listId = list.Id;
        }

        Guid? categoryId = null;
        if (!string.IsNullOrWhiteSpace(selection.CategoryName))
        {
            var category = await FindCategoryAsync(accounts, selection.CategoryName).ConfigureAwait(false);
            if (category is null)
                return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoCategory, selection.CategoryName.Trim()));

            categoryId = category.Id;
        }

        var name = selection.Name?.Trim();
        var query = await ResolvePersonAsync(name, command.People).ConfigureAwait(false) ?? name;
        var page = await contactService.GetContactsPageAsync(
            new ContactQueryFilter(ListId: listId, AccountId: accountId, FavoritesOnly: selection.FavoritesOnly, SearchQuery: query ?? string.Empty, CategoryId: categoryId),
            0,
            ContactPageSize).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // The same person can appear once per address book; one entry per name and address is enough.
        var contacts = (page?.Contacts ?? [])
            .DistinctBy(static contact => contact.Id)
            .DistinctBy(static contact => ((contact.DisplayName ?? string.Empty).Trim().ToLowerInvariant(), (contact.PrimaryEmailAddress ?? string.Empty).Trim().ToLowerInvariant()))
            .ToList();
        if (selection.Limit > 0)
            contacts = contacts.Take(selection.Limit).ToList();

        if (contacts.Count == 0)
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoContacts, PlainText(step)));

        var isMany = selection.All || selection.Limit > 1 || command.Action == VoiceAction.FindContacts;
        if (!isMany && contacts.Count > 1)
        {
            var exact = name is { Length: > 0 } wanted ? contacts.Where(contact => SameText(contact.DisplayName, wanted)).ToList() : [];
            if (exact.Count == 0 && name is { Length: > 0 } address && address.Contains('@'))
                exact = contacts.Where(contact => SameText(contact.PrimaryEmailAddress, address)).ToList();
            if (exact.Count != 1)
            {
                var names = contacts.Take(3).Select(static contact => $"“{ContactTitle(contact)}”");
                return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_WhichContact, contacts.Count, string.Join(", ", names)));
            }

            contacts = exact;
        }

        var targets = contacts.Select(contact => new VoiceContactTarget(contact, accounts.FirstOrDefault(account => account.Id == contact.MailAccountId))).ToList();
        state.ContactTargets[step.CommandIndex] = targets;
        return StepOutcome.Keep(DescribeContactTargets(targets, step.Detail));
    }

    private static ContactList? MatchContactList(IEnumerable<ContactList> lists, string name)
    {
        var all = lists.ToList();
        return all.FirstOrDefault(list => SameText(list.Name, name))
            ?? all.FirstOrDefault(list => Fits(list.Name, name) || Fits(name, list.Name));
    }

    private async Task<MailCategory?> FindCategoryAsync(IEnumerable<MailAccount> accounts, string name)
    {
        var categories = new List<MailCategory>();
        foreach (var account in accounts)
            categories.AddRange(await mailCategoryService.GetCategoriesAsync(account.Id).ConfigureAwait(false) ?? []);

        return categories.FirstOrDefault(category => SameText(category.Name, name))
            ?? categories.FirstOrDefault(category => Fits(category.Name, name));
    }

    private static string DescribeContactTargets(IReadOnlyList<VoiceContactTarget> targets, string? detail)
    {
        if (targets.Count == 1)
        {
            var only = targets[0].Contact;
            var address = only.PrimaryEmailAddress;
            var parts = new[] { ContactTitle(only), string.IsNullOrWhiteSpace(address) || SameText(address, ContactTitle(only)) ? null : address, detail };
            return string.Join(" · ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
        }

        var found = string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_ContactsFound, targets.Count);
        return string.IsNullOrWhiteSpace(detail) ? found : $"{found} · {detail}";
    }

    private static string ContactTitle(AccountContact contact)
        => !string.IsNullOrWhiteSpace(contact.DisplayName) ? contact.DisplayName.Replace("**", string.Empty).Trim()
            : contact.PrimaryEmailAddress ?? Translator.VoiceResult_NoSubject;

    private static string ContactsText(int count)
        => count == 1 ? Translator.VoiceCount_Contact : string.Format(CultureInfo.CurrentCulture, Translator.VoiceCount_Contacts, count);

    private static string ContactName(IReadOnlyList<VoiceContactTarget> targets)
        => targets.Count == 1 ? ContactTitle(targets[0].Contact) : ContactsText(targets.Count);

    private async Task<StepResult> RunContactStepAsync(StepContext context)
    {
        var command = context.Step.Command;
        if (!context.Resolution.ContactTargets.TryGetValue(context.Step.CommandIndex, out var targets) || targets.Count == 0)
            return StepResult.Failed(Translator.VoiceResult_NoContactsLeft);

        switch (command.Action)
        {
            case VoiceAction.FindContacts:
            {
                var names = string.Join(", ", targets.Take(ListedItems).Select(static target => target.Contact.PrimaryEmailAddress is { Length: > 0 } address
                    ? $"{ContactTitle(target.Contact)} <{address}>"
                    : ContactTitle(target.Contact)));
                if (targets.Count > ListedItems)
                    names += string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_AndMore, targets.Count - ListedItems);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_ContactsFound, ContactsText(targets.Count), names));
            }

            case VoiceAction.FavoriteContact or VoiceAction.UnfavoriteContact:
            {
                var favorite = command.Action == VoiceAction.FavoriteContact;
                foreach (var target in targets.Where(target => target.Contact.IsFavorite != favorite))
                {
                    var original = RequestEntityCloner.Contact(target.Contact);
                    var desired = RequestEntityCloner.Contact(target.Contact);
                    desired.IsFavorite = favorite;
                    await requestDelegator.ExecuteLocalAsync(new ApplicationLocalContactRequest(ApplicationLocalContactOperation.SetFavorite, desired, original)).ConfigureAwait(false);
                }

                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, favorite ? Translator.VoiceResult_Favorited : Translator.VoiceResult_Unfavorited, ContactName(targets)));
            }

            case VoiceAction.AddContactToList or VoiceAction.RemoveContactFromList:
                return await ChangeListMembershipAsync(command, targets).ConfigureAwait(false);

            case VoiceAction.CategorizeContact:
                return await CategorizeContactsAsync(command, targets).ConfigureAwait(false);

            case VoiceAction.DeleteContact:
            {
                var requests = targets.Select(static target => new ContactOperationPreparationRequest(ContactSynchronizerOperation.Delete, target.Contact)).ToList();
                await requestDelegator.ExecuteAsync(requests).ConfigureAwait(false);
                return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Deleted, ContactName(targets)));
            }
        }

        return StepResult.None;
    }

    /// <summary>A list only takes contacts of its own address book, so each contact goes to the list of that name in its book.</summary>
    private async Task<StepResult> ChangeListMembershipAsync(VoiceCommand command, IReadOnlyList<VoiceContactTarget> targets)
    {
        var name = command.ContactChange?.ListName?.Trim() ?? command.Contacts?.ListName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_List));

        var lists = await contactService.GetContactListsAsync().ConfigureAwait(false) ?? [];
        var adding = command.Action == VoiceAction.AddContactToList;
        var changes = new Dictionary<Guid, (ContactList List, List<Guid> ContactIds)>();
        foreach (var target in targets)
        {
            var list = MatchContactList(lists.Where(list => list.AddressBookId is null || list.AddressBookId == target.Contact.AddressBookId), name);
            if (list is null)
                continue;

            if (!changes.TryGetValue(list.Id, out var entry))
                changes[list.Id] = entry = (list, []);
            entry.ContactIds.Add(target.Contact.Id);
        }

        if (changes.Count == 0)
            return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoContactList, name));

        foreach (var (list, ids) in changes.Values)
        {
            var request = adding
                ? new ContactListRequest(ContactSynchronizerOperation.UpdateListMembers, list, addedContactIds: ids)
                : new ContactListRequest(ContactSynchronizerOperation.UpdateListMembers, list, removedContactIds: ids);
            await requestDelegator.ExecuteAsync(request.MailAccountId, (IRequestBase[])[request]).ConfigureAwait(false);
        }

        var listName = changes.Values.First().List.Name;
        return StepResult.Done(string.Format(CultureInfo.CurrentCulture, adding ? Translator.VoiceResult_AddedToList : Translator.VoiceResult_RemovedFromList, ContactName(targets), listName));
    }

    private async Task<StepResult> CategorizeContactsAsync(VoiceCommand command, IReadOnlyList<VoiceContactTarget> targets)
    {
        var name = command.ContactChange?.CategoryName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Category));

        var changed = 0;
        string? categoryName = null;
        foreach (var accountTargets in targets.GroupBy(static target => target.Contact.MailAccountId))
        {
            var categories = await mailCategoryService.GetCategoriesAsync(accountTargets.Key).ConfigureAwait(false) ?? [];
            var category = categories.FirstOrDefault(category => SameText(category.Name, name)) ?? categories.FirstOrDefault(category => Fits(category.Name, name));
            if (category is null)
                continue;

            var requests = new List<IRequestBase>();
            foreach (var target in accountTargets)
            {
                // The request carries the contact's whole set of categories, so it starts from the stored contact.
                var contact = await contactService.GetContactAsync(target.Contact.Id).ConfigureAwait(false) ?? target.Contact;
                var current = contact.Categories ?? [];
                if (current.Any(existing => existing.Id == category.Id))
                    continue;

                requests.Add(new ContactCategoryRequest(contact, current.Append(category)));
            }

            if (requests.Count > 0)
                await requestDelegator.ExecuteAsync(category.MailAccountId, requests).ConfigureAwait(false);

            changed += requests.Count;
            categoryName ??= category.Name;
        }

        return categoryName is null
            ? StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoCategory, name))
            : StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Categorized, ContactName(targets), categoryName));
    }

    /// <summary>Creates a contact in the address book the contact editor would pick, in the named account when there is one.</summary>
    private async Task<StepResult> CreateContactAsync(StepContext context)
    {
        if (context.Step.Command.ContactChange is not { } change
            || (string.IsNullOrWhiteSpace(change.Name) && string.IsNullOrWhiteSpace(change.Email) && string.IsNullOrWhiteSpace(change.Phone)))
            return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Contact));

        var named = context.Step.Command.Mailbox?.Account is not null ? context.Group.Account?.Id : null;
        var destinations = await contactService.GetCreateDestinationsAsync().ConfigureAwait(false) ?? [];
        var destination = VoiceContactDestinations.Pick(destinations, preferencesService, named)
            ?? VoiceContactDestinations.Pick(destinations, preferencesService, null);
        if (destination is null)
            return StepResult.Failed(Translator.VoiceResult_NoAddressBook);

        var id = Guid.NewGuid();
        var email = change.Email?.Trim();
        var phone = change.Phone?.Trim();
        var displayName = !string.IsNullOrWhiteSpace(change.Name) ? change.Name.Trim() : email ?? phone ?? string.Empty;
        var contact = new AccountContact
        {
            Id = id,
            MailAccountId = destination.MailAccountId,
            AddressBookId = destination.AddressBookId,
            SourceKind = destination.SourceKind,
            DisplayName = displayName,
            EmailAddresses = string.IsNullOrWhiteSpace(email)
                ? []
                : [new ContactEmailAddress { Id = Guid.NewGuid(), ContactId = id, Address = email, NormalizedAddress = ContactEmailAddress.Normalize(email), IsPrimary = true }],
            PhoneNumbers = string.IsNullOrWhiteSpace(phone)
                ? []
                : [new ContactPhoneNumber { Id = Guid.NewGuid(), ContactId = id, Number = phone, Kind = ContactPhoneKind.Mobile, IsPrimary = true }]
        };

        await requestDelegator.ExecuteAsync(new ContactOperationPreparationRequest(ContactSynchronizerOperation.Create, contact)).ConfigureAwait(false);
        return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_ContactCreated, displayName));
    }
}
