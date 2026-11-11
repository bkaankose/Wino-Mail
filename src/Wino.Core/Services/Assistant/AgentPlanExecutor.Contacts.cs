#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Agent.Runtime.Plan;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Requests;
using Wino.Core.Requests.Contact;

namespace Wino.Core.Services.Assistant;

/// <summary>
/// Contact steps: the device reads the contacts a <see cref="ContactQuery"/> describes (resolved addresses, words of
/// the name, a list, a category, favorites) or the exact contacts a selection names by address, and changes them with
/// the same requests the Contacts page queues.
/// </summary>
public sealed partial class AgentPlanExecutor
{
    private const int ContactPageSize = 200;

    // ---- targets -------------------------------------------------------------------------------------------------

    private async Task<List<AccountContact>> ResolveContactsAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (run.Contacts.TryGetValue(step.N, out var cached))
            return cached;

        List<AccountContact> contacts;
        switch (step.Target)
        {
            case StepTarget.Contacts query:
                contacts = await FindContactsAsync(query.Query, ct).ConfigureAwait(false);
                break;

            case StepTarget.ContactItems items:
                contacts = await LoadContactsAsync(items.Items, ct).ConfigureAwait(false);
                break;

            case StepTarget.FromStep { Part: "items" } from:
                contacts = run.Contacts.TryGetValue(from.N, out var source)
                    ? source.ToList()
                    : await LoadContactsAsync(TouchedOf(run, from.N, ItemKind.Contact), ct).ConfigureAwait(false);
                break;

            case StepTarget.FromStep from:
            {
                // "Add the sender to favorites": the people of the earlier step are looked up among the contacts.
                var people = await PeopleOfStepAsync(run, from.N, from.Part, ct).ConfigureAwait(false);
                contacts = await LoadByAddressesAsync(people.Select(static person => person.Address), ct).ConfigureAwait(false);
                break;
            }

            default:
                contacts = [];
                break;
        }

        run.Contacts[step.N] = contacts;
        return contacts;
    }

    /// <summary>Contacts a selection names: by address (the shared id convention), or by contact id when an address is not known.</summary>
    private async Task<List<AccountContact>> LoadContactsAsync(IEnumerable<ItemRef> items, CancellationToken ct)
    {
        var refs = items.Where(static item => item.Kind == ItemKind.Contact).ToList();
        var contacts = await LoadByAddressesAsync(refs.Where(static item => item.Id.Contains('@')).Select(static item => item.Id), ct).ConfigureAwait(false);
        foreach (var item in refs.Where(static item => !item.Id.Contains('@')))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(item.Id, out var id))
                continue;

            try
            {
                if (await contactService.GetContactAsync(id).ConfigureAwait(false) is { } contact)
                    contacts.Add(contact);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug(ex, "Could not load a contact of an assistant step.");
            }
        }

        return Distinct(contacts);
    }

    private async Task<List<AccountContact>> LoadByAddressesAsync(IEnumerable<string> addresses, CancellationToken ct)
    {
        var wanted = addresses.Where(static address => !string.IsNullOrWhiteSpace(address)).Select(static address => address.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0)
            return [];

        ct.ThrowIfCancellationRequested();
        try
        {
            return Distinct(await contactService.GetContactsByAddressesAsync(null, wanted).ConfigureAwait(false) ?? []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Could not look up contacts by address for an assistant step.");
            return [];
        }
    }

    private async Task<List<AccountContact>> FindContactsAsync(ContactQuery query, CancellationToken ct)
    {
        List<AccountContact> contacts;
        if (query.Addresses.Count > 0)
        {
            contacts = await LoadByAddressesAsync(query.Addresses, ct).ConfigureAwait(false);
            if (query.ListId is Guid listId)
            {
                var members = (await contactService.GetContactsPageAsync(new ContactQueryFilter(ListId: listId), 0, ContactPageSize).ConfigureAwait(false))?.Contacts ?? [];
                var ids = members.Select(static member => member.Id).ToHashSet();
                contacts = contacts.Where(contact => ids.Contains(contact.Id)).ToList();
            }

            if (query.CategoryId is Guid categoryId)
                contacts = contacts.Where(contact => (contact.Categories ?? []).Any(category => category.Id == categoryId)).ToList();
            if (query.FavoritesOnly)
                contacts = contacts.Where(static contact => contact.IsFavorite).ToList();
        }
        else
        {
            var page = await contactService.GetContactsPageAsync(
                new ContactQueryFilter(ListId: query.ListId, FavoritesOnly: query.FavoritesOnly, SearchQuery: Clean(query.NameWords), CategoryId: query.CategoryId),
                0,
                ContactPageSize).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            contacts = Distinct(page?.Contacts ?? []);
        }

        return query.Limit > 0 ? contacts.Take(query.Limit).ToList() : contacts;
    }

    /// <summary>The same person can appear once per address book; one entry per name and address is enough.</summary>
    private static List<AccountContact> Distinct(IEnumerable<AccountContact> contacts)
        => contacts
            .DistinctBy(static contact => contact.Id)
            .DistinctBy(static contact => ((contact.DisplayName ?? string.Empty).Trim().ToLowerInvariant(), (contact.PrimaryEmailAddress ?? string.Empty).Trim().ToLowerInvariant()))
            .ToList();

    private static ItemRef Ref(AccountContact contact)
        => string.IsNullOrWhiteSpace(contact.PrimaryEmailAddress)
            ? new ItemRef(ItemKind.Contact, contact.MailAccountId, contact.Id.ToString())
            : ItemRef.Contact(contact.PrimaryEmailAddress);

    private static List<ItemRef> Refs(IEnumerable<AccountContact> contacts) => contacts.Select(Ref).ToList();

    private static string ContactTitle(AccountContact contact)
        => !string.IsNullOrWhiteSpace(contact.DisplayName) ? Plain(contact.DisplayName)
            : contact.PrimaryEmailAddress ?? Translator.VoiceResult_NoSubject;

    private static string ContactName(IReadOnlyList<AccountContact> contacts)
        => contacts.Count == 1 ? ContactTitle(contacts[0]) : ContactsText(contacts.Count);

    // ---- runners -------------------------------------------------------------------------------------------------

    private async Task<StepOutcome> RunContactStepAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var contacts = await ResolveContactsAsync(step, run, ct).ConfigureAwait(false);
        if (contacts.Count == 0)
            return NothingMatched(step, Translator.VoiceIssue_NoContacts);

        var change = step.Args as StepArgs.ContactChangeArgs;
        switch (step.Action)
        {
            case AgentAction.FindContacts:
            {
                var names = string.Join(", ", contacts.Take(ListedItems).Select(static contact => contact.PrimaryEmailAddress is { Length: > 0 } address && !SameText(address, ContactTitle(contact))
                    ? $"{ContactTitle(contact)} <{address}>"
                    : ContactTitle(contact)));
                if (contacts.Count > ListedItems)
                    names += string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_AndMore, contacts.Count - ListedItems);
                return Done(step, Refs(contacts), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_ContactsFound, ContactsText(contacts.Count), names));
            }

            case AgentAction.FavoriteContact or AgentAction.UnfavoriteContact:
            {
                var favorite = step.Action == AgentAction.FavoriteContact;
                foreach (var contact in contacts.Where(contact => contact.IsFavorite != favorite))
                {
                    var original = RequestEntityCloner.Contact(contact);
                    var desired = RequestEntityCloner.Contact(contact);
                    desired.IsFavorite = favorite;
                    await requestDelegator.ExecuteLocalAsync(new ApplicationLocalContactRequest(ApplicationLocalContactOperation.SetFavorite, desired, original)).ConfigureAwait(false);
                }

                return Done(step, Refs(contacts), string.Format(CultureInfo.CurrentCulture, favorite ? Translator.VoiceResult_Favorited : Translator.VoiceResult_Unfavorited, ContactName(contacts)));
            }

            case AgentAction.AddContactToList or AgentAction.RemoveContactFromList:
                return await ChangeListMembershipAsync(step, change, contacts).ConfigureAwait(false);

            case AgentAction.CategorizeContact:
                return await CategorizeContactsAsync(step, change, contacts).ConfigureAwait(false);

            case AgentAction.DeleteContact:
            {
                var requests = contacts.Select(static contact => new ContactOperationPreparationRequest(ContactSynchronizerOperation.Delete, contact)).ToList();
                await requestDelegator.ExecuteAsync(requests).ConfigureAwait(false);
                return Done(step, Refs(contacts), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Deleted, ContactName(contacts)));
            }
        }

        return Failed(step, Translator.VoiceIssue_Missing_Change);
    }

    /// <summary>A list only takes contacts of its own address book, so contacts of other books are left out.</summary>
    private async Task<StepOutcome> ChangeListMembershipAsync(ResolvedStep step, StepArgs.ContactChangeArgs? change, IReadOnlyList<AccountContact> contacts)
    {
        if (change?.ListId is not Guid listId)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_List));

        var list = await contactService.GetContactListAsync(listId).ConfigureAwait(false);
        if (list is null)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_List));

        var adding = step.Action == AgentAction.AddContactToList;
        var members = contacts.Where(contact => list.AddressBookId is null || list.AddressBookId == contact.AddressBookId).ToList();
        if (members.Count == 0)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoContactList, Plain(list.Name ?? string.Empty)));

        var ids = members.Select(static contact => contact.Id).ToList();
        var request = adding
            ? new ContactListRequest(ContactSynchronizerOperation.UpdateListMembers, list, addedContactIds: ids)
            : new ContactListRequest(ContactSynchronizerOperation.UpdateListMembers, list, removedContactIds: ids);
        await requestDelegator.ExecuteAsync(request.MailAccountId, (IRequestBase[])[request]).ConfigureAwait(false);

        return Done(step, Refs(members), string.Format(CultureInfo.CurrentCulture, adding ? Translator.VoiceResult_AddedToList : Translator.VoiceResult_RemovedFromList, ContactName(members), Plain(list.Name ?? string.Empty)));
    }

    /// <summary>A category belongs to one account, so only that account's contacts take it.</summary>
    private async Task<StepOutcome> CategorizeContactsAsync(ResolvedStep step, StepArgs.ContactChangeArgs? change, IReadOnlyList<AccountContact> contacts)
    {
        if (change?.CategoryId is not Guid categoryId)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Category));

        var category = await mailCategoryService.GetCategoryAsync(categoryId).ConfigureAwait(false);
        if (category is null)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Category));

        var requests = new List<IRequestBase>();
        var changed = new List<AccountContact>();
        foreach (var target in contacts.Where(contact => contact.MailAccountId == category.MailAccountId))
        {
            // The request carries the contact's whole set of categories, so it starts from the stored contact.
            var contact = await contactService.GetContactAsync(target.Id).ConfigureAwait(false) ?? target;
            var current = contact.Categories ?? [];
            if (current.Any(existing => existing.Id == category.Id))
                continue;

            requests.Add(new ContactCategoryRequest(contact, current.Append(category)));
            changed.Add(contact);
        }

        if (requests.Count > 0)
            await requestDelegator.ExecuteAsync(category.MailAccountId, requests).ConfigureAwait(false);

        return Done(step, Refs(changed.Count > 0 ? changed : contacts), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Categorized, ContactName(contacts), Plain(category.Name ?? string.Empty)));
    }

    // ---- creation ------------------------------------------------------------------------------------------------

    /// <summary>Creates a contact in the address book the contact editor would pick.</summary>
    private async Task<StepOutcome> CreateContactAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (step.Args is not StepArgs.ContactChangeArgs change
            || (string.IsNullOrWhiteSpace(change.Name) && string.IsNullOrWhiteSpace(change.Email) && string.IsNullOrWhiteSpace(change.Phone)))
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Contact));

        var destinations = await contactService.GetCreateDestinationsAsync().ConfigureAwait(false) ?? [];
        var destination = PickDestination(destinations);
        if (destination is null)
            return Failed(step, Translator.VoiceResult_NoAddressBook);

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
        run.Contacts[step.N] = [contact];
        return Done(step, [Ref(contact)], string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_ContactCreated, Plain(displayName)));
    }

    /// <summary>The address book the contact editor would pick: the preferred one, else a provider book, else the default.</summary>
    private ContactCreateDestination? PickDestination(IEnumerable<ContactCreateDestination> destinations)
    {
        var writable = destinations.Where(static destination => !destination.IsReadOnly).ToList();
        var preferred = preferencesService.ContactCreationBehavior switch
        {
            NewItemDestinationBehavior.Specific => preferencesService.SpecificContactAddressBookId,
            NewItemDestinationBehavior.LastUsed => preferencesService.LastUsedContactAddressBookId,
            _ => null
        };

        return writable.FirstOrDefault(destination => destination.AddressBookId == preferred)
            ?? writable.OrderByDescending(static destination => destination.SourceKind != ContactSourceKind.Local)
                .ThenByDescending(static destination => destination.IsDefault)
                .FirstOrDefault();
    }
}
