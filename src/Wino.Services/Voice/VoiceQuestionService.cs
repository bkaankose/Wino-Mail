#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Services.Voice;

public sealed class VoiceQuestionService(
    IAccountService accountService,
    IFolderService folderService,
    ICalendarService calendarService,
    IContactService contactService,
    IRecipientHistoryService recipientHistory,
    IMailService mailService) : IVoiceQuestionService
{
    private const int TopPeoplePerAccount = 25;
    private const int SearchLimit = 40;
    private const int RecentMailsPerAccount = 300;

    private static readonly HashSet<SpecialFolderType> HiddenFolderTypes =
    [
        SpecialFolderType.Chat,
        SpecialFolderType.Category,
        SpecialFolderType.More,
        SpecialFolderType.Unread
    ];

    public IReadOnlyList<VoiceQuestion> GetQuestions(VoicePlan plan)
    {
        var questions = new List<VoiceQuestion>();
        foreach (var command in plan.Commands ?? [])
        {
            foreach (var reference in command.Unresolved ?? [])
            {
                var phrase = reference.Phrase?.Trim() ?? string.Empty;
                if (reference.Kind is not (UnresolvedKind.DefaultCalendar or UnresolvedKind.DefaultAccount) && phrase.Length == 0)
                    continue;

                if (questions.Any(existing => existing.Kind == reference.Kind && SamePhrase(existing.Phrase, phrase)))
                    continue;

                questions.Add(new VoiceQuestion(reference.Kind, phrase, reference.IsRelationship, Ask(reference.Kind, phrase, reference.IsRelationship)));
            }
        }

        return questions;
    }

    public async Task<VoicePlan> AddLocalQuestionsAsync(VoicePlan plan, CancellationToken cancellationToken = default)
    {
        try
        {
            var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
            if (accounts.Count == 0)
                return plan;

            var commands = new List<VoiceCommand>();
            foreach (var command in plan.Commands ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                commands.Add(await AddLocalQuestionsAsync(command, accounts, cancellationToken).ConfigureAwait(false));
            }

            return plan with { Commands = commands };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not check the voice plan against local accounts, folders and contacts.");
            return plan;
        }
    }

    private async Task<VoiceCommand> AddLocalQuestionsAsync(VoiceCommand command, List<MailAccount> accounts, CancellationToken cancellationToken)
    {
        if (command.Action is VoiceAction.NotActionable or VoiceAction.CanceledByUser)
            return command;

        var added = new List<UnresolvedReference>();
        var targets = accounts;
        if (command.Mailbox is { Scope: MailboxScope.Single, Account: { } reference })
        {
            var matches = VoicePlanPreviewBuilder.MatchAccounts(reference, accounts);
            if (matches.Count == 1)
            {
                targets = matches;
            }
            else if (matches.Count == 0 && command.Mail is { FolderName: null or "", FolderSpecialType: null or SpecialFolderKind.None } mail
                && reference.Provider is null && string.IsNullOrWhiteSpace(reference.Address) && reference.Position is null
                && !string.IsNullOrWhiteSpace(reference.Name)
                && await AnyFolderNamedAsync(accounts, reference.Name.Trim(), cancellationToken).ConfigureAwait(false))
            {
                // "unread mails in demo": the planner took a folder for an account name. No account has the name and a
                // folder does, so the command searches that folder in every account.
                command = command with
                {
                    Mailbox = new MailboxSelection(MailboxScope.All, null, MailboxMatch.Unspecified, null),
                    Mail = mail with { FolderName = reference.Name.Trim(), Scope = FolderScope.Folder }
                };
            }
            else if (accounts.Count > 1 && !string.IsNullOrWhiteSpace(reference.Name))
            {
                added.Add(new UnresolvedReference(UnresolvedKind.Account, reference.Name.Trim()));
            }
            else if (matches.Count > 1 && reference.Provider is { } provider)
            {
                // "My Outlook" with two Outlook accounts: asked like an unknown name instead of failing as a banner.
                added.Add(new UnresolvedReference(UnresolvedKind.Account, provider.ToString()));
            }
        }

        if (command.Mail is { FolderId: null, FolderName: { } folderName } && !string.IsNullOrWhiteSpace(folderName)
            && !await AnyFolderNamedAsync(targets, folderName.Trim(), cancellationToken).ConfigureAwait(false))
        {
            added.Add(new UnresolvedReference(UnresolvedKind.Folder, folderName.Trim()));
        }

        var people = (command.Draft?.To ?? []).Concat(command.Draft?.Cc ?? []).Concat(command.Event?.Attendees ?? []);
        var identified = command.People?.ToList() ?? [];
        foreach (var raw in people.Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            var name = raw?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Contains('@') || VoiceResultTokens.IsToken(name)
                || identified.Exists(person => SamePhrase(person.Name, name))
                || command.Unresolved?.Any(reference => reference.Kind == UnresolvedKind.Group && SamePhrase(reference.Phrase, name)) == true)
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            // The same sources the picker offers: correspondents and contacts. One person answering to the name is
            // used (by address hash, as Personal mode does); none or several are asked about.
            var candidates = (await FindPeopleAsync(accounts, name, cancellationToken).ConfigureAwait(false))
                .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Address))
                .ToList();
            var exact = candidates.Where(candidate => SameName(candidate.Title, name)).ToList();
            var chosen = exact.Count == 1 ? exact[0] : candidates.Count == 1 ? candidates[0] : null;
            if (chosen?.Address is { } address)
                identified.Add(new PersonIdentity(name, VoiceBrainHash.Address(address)));
            else
                added.Add(new UnresolvedReference(UnresolvedKind.Person, name));
        }

        if (identified.Count > (command.People?.Count ?? 0))
            command = command with { People = identified };

        if (added.Count == 0)
            return command;

        var unresolved = command.Unresolved?.ToList() ?? [];
        foreach (var question in added)
        {
            if (!unresolved.Exists(existing => existing.Kind == question.Kind && SamePhrase(existing.Phrase, question.Phrase)))
                unresolved.Add(question);
        }

        return command with { Unresolved = unresolved };
    }

    private async Task<bool> AnyFolderNamedAsync(List<MailAccount> accounts, string name, CancellationToken cancellationToken)
    {
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folders = await folderService.GetFoldersAsync(account.Id).ConfigureAwait(false) ?? [];
            if (folders.Any(folder => NamedLike(folder.DisplayName, name) || NamedLike(folder.FolderName, name)))
                return true;
        }

        return false;
    }

    private static bool NamedLike(string? value, string name)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(name, StringComparison.CurrentCultureIgnoreCase);

    public async Task<IReadOnlyList<VoiceAnswerCandidate>> GetCandidatesAsync(VoiceQuestion question, string? filter, CancellationToken cancellationToken = default)
    {
        var text = filter?.Trim() ?? string.Empty;
        try
        {
            var accounts = (await accountService.GetAccountsAsync().ConfigureAwait(false) ?? [])
                .OrderBy(static account => account.Order)
                .ThenBy(static account => account.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();

            return question.Kind switch
            {
                UnresolvedKind.Person when text.Length == 0 && !question.IsRelationship && question.Phrase.Length >= 2
                    => await GetNamedPeopleFirstAsync(accounts, question.Phrase, cancellationToken).ConfigureAwait(false),
                UnresolvedKind.Person => await GetPeopleAsync(accounts, text, cancellationToken).ConfigureAwait(false),
                UnresolvedKind.Account => accounts
                    .Where(account => Matches(text, AccountTitle(account), account.Address))
                    .Select(static account => new VoiceAnswerCandidate($"a:{account.Id:N}", AccountTitle(account), Distinct(account.Address, AccountTitle(account)), null, account))
                    .ToList(),
                UnresolvedKind.Folder => await GetFoldersAsync(accounts, text, cancellationToken).ConfigureAwait(false),
                UnresolvedKind.DefaultCalendar => await GetCalendarsAsync(accounts, text, cancellationToken).ConfigureAwait(false),
                UnresolvedKind.Group => await GetPeopleAsync(accounts, text, cancellationToken).ConfigureAwait(false),
                UnresolvedKind.Sender => await GetSendersAsync(accounts, text, cancellationToken).ConfigureAwait(false),
                UnresolvedKind.DefaultAccount => accounts
                    .Where(account => question.Phrase != nameof(BrainDefaultUse.Task) || (account.IsTaskAccessGranted && account.IsTaskAccessEnabled))
                    .Where(account => Matches(text, AccountTitle(account), account.Address))
                    .Select(static account => new VoiceAnswerCandidate($"a:{account.Id:N}", AccountTitle(account), Distinct(account.Address, AccountTitle(account)), null, account))
                    .ToList(),
                UnresolvedKind.Filter =>
                [
                    new VoiceAnswerCandidate("k:Person", Translator.VoiceQuestion_Filter_Person, null, null, null, SenderKind: MailSenderKind.Person),
                    new VoiceAnswerCandidate("k:Service", Translator.VoiceQuestion_Filter_Service, null, null, null, SenderKind: MailSenderKind.Service),
                    new VoiceAnswerCandidate("k:Newsletter", Translator.VoiceQuestion_Filter_Newsletter, null, null, null, SenderKind: MailSenderKind.Newsletter),
                ],
                _ => []
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the choices for a voice question.");
            return [];
        }
    }

    public async Task<VoiceKnownAnswers> AnswerKnownQuestionsAsync(VoicePlan plan, CancellationToken cancellationToken = default)
    {
        var facts = new List<AddBrainFactRequest>();
        try
        {
            var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
            foreach (var question in GetQuestions(plan))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var answer = await FindKnownAnswerAsync(question, accounts, cancellationToken).ConfigureAwait(false);
                if (answer is null)
                    continue;

                plan = ApplyAnswer(plan, question, answer);
                if (CreateFact(question, answer) is { } fact)
                    facts.Add(fact);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not answer voice questions on the device.");
        }

        return new VoiceKnownAnswers(plan, facts);
    }

    private async Task<VoiceAnswerCandidate?> FindKnownAnswerAsync(VoiceQuestion question, List<MailAccount> accounts, CancellationToken cancellationToken)
    {
        var phrase = question.Phrase.Trim();
        if (phrase.Length == 0 || accounts.Count == 0)
            return null;

        switch (question.Kind)
        {
            case UnresolvedKind.Account:
            {
                var named = accounts.Where(account => SameName(account.Name, phrase) || SameName(account.SenderName, phrase)
                    || string.Equals(account.Address, phrase, StringComparison.OrdinalIgnoreCase)
                    || (account.Address?.IndexOf('@') is int at && at > 0 && SameName(account.Address[..at], phrase))).ToList();
                if (named is not [var account])
                    return null;

                return (await GetCandidatesAsync(question, null, cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(candidate => candidate.Account?.Id == account.Id);
            }
            case UnresolvedKind.Folder:
            {
                var folders = (await GetCandidatesAsync(question, phrase, cancellationToken).ConfigureAwait(false))
                    .Where(candidate => candidate.Folder is not null && SameName(candidate.Title, phrase))
                    .ToList();
                return folders is [var folder] ? folder : null;
            }
            case UnresolvedKind.Person when !question.IsRelationship:
            {
                var people = (await FindPeopleAsync(accounts, phrase, cancellationToken).ConfigureAwait(false))
                    .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Address))
                    .ToList();
                var exact = people.Where(candidate => SameName(candidate.Title, phrase)).ToList();
                return exact.Count == 1 ? exact[0] : people.Count == 1 ? people[0] : null;
            }
            default:
                return null;
        }
    }

    public VoicePlan ApplyAnswer(VoicePlan plan, VoiceQuestion question, VoiceAnswerCandidate answer)
    {
        var commands = (plan.Commands ?? []).Select(command => Apply(command, question, answer)).ToList();
        return plan with { Commands = commands };
    }

    public AddBrainFactRequest? CreateFact(VoiceQuestion question, VoiceAnswerCandidate answer)
    {
        var label = question.Phrase.Length > BrainFact.MaxLabelLength ? question.Phrase[..BrainFact.MaxLabelLength] : question.Phrase;
        var mailbox = answer.Account is { } account ? VoiceProviderKinds.ToMailboxKey(account) : null;

        return question.Kind switch
        {
            UnresolvedKind.Person when !string.IsNullOrWhiteSpace(answer.Address)
                => new AddBrainFactRequest(BrainFactKind.Person, label, null, null, answer.Title, VoiceBrainHash.Address(answer.Address)),
            UnresolvedKind.Account when mailbox is not null
                => new AddBrainFactRequest(BrainFactKind.AccountNickname, label, mailbox, null, answer.Title, null),
            UnresolvedKind.Folder when mailbox is not null && answer.Folder is { } folder && !string.IsNullOrWhiteSpace(folder.RemoteFolderId)
                => new AddBrainFactRequest(BrainFactKind.FolderSynonym, label, mailbox, folder.RemoteFolderId, answer.Title, null),
            UnresolvedKind.DefaultCalendar when mailbox is not null && answer.Calendar is { } calendar && !string.IsNullOrWhiteSpace(calendar.RemoteCalendarId)
                => new AddBrainFactRequest(BrainFactKind.DefaultCalendar, string.Empty, mailbox, calendar.RemoteCalendarId, answer.Title, null),
            UnresolvedKind.Sender when !string.IsNullOrWhiteSpace(answer.Address)
                => new AddBrainFactRequest(BrainFactKind.SenderAlias, label, null, answer.Address, answer.Title, null),
            UnresolvedKind.Group when answer.Members is { Count: > 0 } members
                => new AddBrainFactRequest(BrainFactKind.Group, label, null, null, null, null,
                    Members: members
                        .Where(static member => !string.IsNullOrWhiteSpace(member.Address))
                        .Take(BrainFact.MaxGroupMembers)
                        .Select(static member => new BrainContact(member.Title, VoiceBrainHash.Address(member.Address!)))
                        .ToList()),
            UnresolvedKind.DefaultAccount when mailbox is not null && Enum.TryParse<BrainDefaultUse>(question.Phrase, out var use)
                => new AddBrainFactRequest(BrainFactKind.DefaultAccount, string.Empty, mailbox, null, answer.Title, null, DefaultUse: use),
            UnresolvedKind.Filter when answer.SenderKind is { } kind
                => new AddBrainFactRequest(BrainFactKind.FilterSynonym, label, null, null, answer.Title, null, SenderKind: kind),
            _ => null
        };
    }

    private static VoiceCommand Apply(VoiceCommand command, VoiceQuestion question, VoiceAnswerCandidate answer)
    {
        var asked = command.Unresolved?.Any(reference => reference.Kind == question.Kind && SamePhrase(reference.Phrase, question.Phrase)) == true;
        var remaining = command.Unresolved?
            .Where(reference => !(reference.Kind == question.Kind && SamePhrase(reference.Phrase, question.Phrase)))
            .ToList();
        var result = command with { Unresolved = remaining is { Count: > 0 } ? remaining : null };

        switch (question.Kind)
        {
            case UnresolvedKind.Person when !string.IsNullOrWhiteSpace(answer.Address):
                return ReplacePerson(result, question.Phrase, answer.Address);

            case UnresolvedKind.Account when answer.Account is { } account:
                if (asked || SamePhrase(result.Mailbox?.Account?.Name, question.Phrase))
                    result = result with { Mailbox = ForAccount(result.Mailbox, account) };
                return result;

            case UnresolvedKind.Folder when answer.Account is { } account && answer.Folder is { } folder:
                if (result.Mail is { } mail && (asked || SamePhrase(mail.FolderName, question.Phrase)))
                {
                    result = result with
                    {
                        Mail = mail with { FolderId = folder.RemoteFolderId, FolderName = FolderName(folder), FolderSpecialType = null },
                        Mailbox = ForAccount(result.Mailbox, account)
                    };
                }
                return result;

            case UnresolvedKind.Group when answer.Members is { Count: > 0 } members:
                return ReplaceGroup(result, question.Phrase, members.Select(static member => member.Address).OfType<string>().ToList());

            case UnresolvedKind.Sender when !string.IsNullOrWhiteSpace(answer.Address):
                if (result.Mail is { } senderMail && (asked || SamePhrase(senderMail.Sender, question.Phrase)))
                    result = result with { Mail = senderMail with { Sender = answer.Address } };
                return result;

            case UnresolvedKind.DefaultAccount when answer.Account is { } defaultAccount:
                return result with { Mailbox = ForAccount(result.Mailbox, defaultAccount) };

            case UnresolvedKind.Filter when answer.SenderKind is { } senderKind:
                if (result.Mail is { } filtered)
                    result = result with { Mail = filtered with { SenderKind = senderKind } };
                return result;

            case UnresolvedKind.DefaultCalendar when answer.Account is { } account && answer.Calendar is { } calendar:
                if (result.Event is { } details && (asked || string.IsNullOrWhiteSpace(details.CalendarId)) && result.Action == VoiceAction.CreateEvent)
                {
                    result = result with
                    {
                        Event = details with { CalendarId = calendar.RemoteCalendarId },
                        Mailbox = ForAccount(result.Mailbox, account)
                    };
                }
                return result;

            default:
                return result;
        }
    }

    private static VoiceCommand ReplacePerson(VoiceCommand command, string phrase, string address)
    {
        List<string>? Replace(List<string>? values)
            => values?.Select(value => SamePhrase(value, phrase) ? address : value).ToList();

        var result = command;
        if (command.Draft is { } draft)
            result = result with { Draft = draft with { To = Replace(draft.To) ?? [], Cc = Replace(draft.Cc) ?? [] } };
        if (command.Event is { } details)
            result = result with { Event = details with { Attendees = Replace(details.Attendees) ?? [] } };
        if (command.Mail is { } mail && SamePhrase(mail.Sender, phrase))
            result = result with { Mail = mail with { Sender = address } };
        if (command.Events is { } selection && SamePhrase(selection.Attendee, phrase))
            result = result with { Events = selection with { Attendee = address } };

        return result;
    }

    private static VoiceCommand ReplaceGroup(VoiceCommand command, string phrase, List<string> addresses)
    {
        List<string> Replace(List<string> values)
            => values.SelectMany(value => SamePhrase(value, phrase) ? addresses : [value]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var result = command;
        if (command.Draft is { } draft)
            result = result with { Draft = draft with { To = Replace(draft.To ?? []), Cc = Replace(draft.Cc ?? []) } };
        if (command.Event is { } details)
            result = result with { Event = details with { Attendees = Replace(details.Attendees ?? []) } };

        return result;
    }

    /// <summary>
    /// Organizations the user gets mail from: automated senders of recent mail, one per domain, named as their mails
    /// name them. The domain is what a sender alias remembers.
    /// </summary>
    private async Task<List<VoiceAnswerCandidate>> GetSendersAsync(List<MailAccount> accounts, string text, CancellationToken cancellationToken)
    {
        var senders = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folders = (await folderService.GetFoldersAsync(account.Id).ConfigureAwait(false) ?? [])
                .Where(static folder => folder.SpecialFolderType == SpecialFolderType.Inbox)
                .Cast<IMailItemFolder>()
                .ToList();
            if (folders.Count == 0)
                continue;

            var options = new MailListInitializationOptions(folders, FilterOptionType.All, SortingOptionType.ReceiveDate,
                CreateThreads: false, IsFocusedOnly: null, SearchQuery: string.Empty, DeduplicateByServerId: true) { Take = RecentMailsPerAccount };
            var page = await mailService.FetchMailPageAsync(options, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var mail in page.Items)
            {
                var address = mail.FromAddress?.Trim() ?? string.Empty;
                var at = address.LastIndexOf('@');
                if (at <= 0 || at == address.Length - 1 || MailSenderClassifier.Classify(address, mail.FromName) == MailSenderKind.Person)
                    continue;

                var domain = address[(at + 1)..].ToLowerInvariant();
                var name = string.IsNullOrWhiteSpace(mail.FromName) ? domain : mail.FromName.Trim();
                senders[domain] = senders.TryGetValue(domain, out var known) ? (known.Name, known.Count + 1) : (name, 1);
            }
        }

        return senders
            .Where(entry => Matches(text, entry.Value.Name, entry.Key))
            .OrderByDescending(static entry => entry.Value.Count)
            .ThenBy(static entry => entry.Value.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(SearchLimit)
            .Select(static entry => new VoiceAnswerCandidate($"s:{entry.Key}", entry.Value.Name, Distinct(entry.Key, entry.Value.Name), null, null, Address: entry.Key))
            .ToList();
    }

    private static MailboxSelection ForAccount(MailboxSelection? current, MailAccount account)
        => new(
            MailboxScope.Single,
            null,
            MailboxMatch.Address,
            current?.Hint,
            new AccountReference(VoiceProviderKinds.ToProviderKind(account.ProviderType), account.Address, null, null));

    // "Who is Burak?": the people answering to the name come first, then the usual frequent correspondents.
    private async Task<List<VoiceAnswerCandidate>> GetNamedPeopleFirstAsync(List<MailAccount> accounts, string name, CancellationToken cancellationToken)
    {
        var named = await FindPeopleAsync(accounts, name, cancellationToken).ConfigureAwait(false);
        var frequent = await GetPeopleAsync(accounts, string.Empty, cancellationToken).ConfigureAwait(false);
        return named.Concat(frequent)
            .DistinctBy(static person => person.Key, StringComparer.Ordinal)
            .Take(SearchLimit)
            .ToList();
    }

    /// <summary>
    /// People answering to a spoken name. The stores match text as typed, so a name said without its accents
    /// ("Christian Muller", "Ahmet Kose") or with a title ("Mr Kogan") finds nobody: the name is searched without
    /// titles, then word by word, and kept only when every spoken word starts a word of the person's name or address,
    /// accents ignored.
    /// </summary>
    private async Task<List<VoiceAnswerCandidate>> FindPeopleAsync(List<MailAccount> accounts, string name, CancellationToken cancellationToken)
    {
        var words = NameWords(name);
        if (words.Count == 0)
            return [];

        var direct = await GetPeopleAsync(accounts, string.Join(' ', words), cancellationToken).ConfigureAwait(false);
        if (direct.Count > 0)
            return direct;

        var found = new List<VoiceAnswerCandidate>();
        foreach (var word in words.OrderByDescending(static word => word.Length))
        {
            if (word.Length < 2)
                continue;

            foreach (var query in new[] { word, Fold(word) }.Distinct(StringComparer.Ordinal))
                found.AddRange(await GetPeopleAsync(accounts, query, cancellationToken).ConfigureAwait(false));
        }

        var folded = words.Select(Fold).ToList();
        return found
            .DistinctBy(static person => person.Key, StringComparer.Ordinal)
            .Where(person => MatchesAll(person, folded))
            .ToList();
    }

    private static bool MatchesAll(VoiceAnswerCandidate person, List<string> words)
    {
        var local = person.Address is { } address && address.IndexOf('@') is int at && at > 0 ? address[..at] : person.Address;
        var targets = NameWords(person.Title).Concat(NameWords(local?.Replace('.', ' ').Replace('_', ' ').Replace('-', ' '))).Select(Fold).ToList();
        return words.All(word => targets.Any(target => target.StartsWith(word, StringComparison.Ordinal)));
    }

    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "miss", "dr", "prof", "sir", "madam", "bay", "bayan", "sayın", "sayin", "herr", "frau", "pan", "pani",
        "monsieur", "madame", "mme", "señor", "señora", "senor", "senora", "sr", "sra"
    };

    private static List<string> NameWords(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([' ', '\t', ',', ';', '(', ')', '"'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static word => word.TrimEnd('.'))
                .Where(static word => word.Length > 0 && !Titles.Contains(word))
                .ToList();

    private static bool SameName(string? title, string name)
    {
        var left = NameWords(title).Select(Fold).ToList();
        var right = NameWords(name).Select(Fold).ToList();
        return left.Count > 0 && left.SequenceEqual(right, StringComparer.Ordinal);
    }

    /// <summary>Lower case without accents; Turkish dotless and dotted i fold to i.</summary>
    private static string Fold(string text)
    {
        var normalized = text.Replace('ı', 'i').Replace('İ', 'i').Replace('ł', 'l').Replace('Ł', 'l').Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private async Task<List<VoiceAnswerCandidate>> GetPeopleAsync(List<MailAccount> accounts, string text, CancellationToken cancellationToken)
    {
        var ranked = new List<(int Score, VoiceAnswerCandidate Candidate)>();
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = text.Length == 0
                ? await recipientHistory.GetMostFrequentAsync(account.Id, TopPeoplePerAccount).ConfigureAwait(false)
                : await recipientHistory.SearchAsync(account.Id, text, SearchLimit).ConfigureAwait(false);

            foreach (var row in rows ?? [])
            {
                if (string.IsNullOrWhiteSpace(row.Address))
                    continue;

                ranked.Add((row.SentCount * 3 + row.ReceivedCount, Person(row.DisplayName, row.Address)));
            }
        }

        var people = ranked
            .OrderByDescending(static entry => entry.Score)
            .Select(static entry => entry.Candidate)
            .ToList();

        if (text.Length >= 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contacts = await contactService.ResolveRecipientCandidatesAsync(null, text, SearchLimit).ConfigureAwait(false) ?? [];
            people.AddRange(contacts
                .Where(static contact => !string.IsNullOrWhiteSpace(contact.PrimaryEmailAddress))
                .Select(static contact => Person(contact.DisplayName, contact.PrimaryEmailAddress)));
        }

        return people
            .DistinctBy(static person => person.Key, StringComparer.Ordinal)
            .Take(SearchLimit)
            .ToList();
    }

    private static VoiceAnswerCandidate Person(string? name, string address)
    {
        var trimmed = address.Trim();
        var title = string.IsNullOrWhiteSpace(name) ? trimmed : name.Trim();
        return new VoiceAnswerCandidate($"p:{BrainMailboxKey.Normalize(trimmed)}", title, Distinct(trimmed, title), null, null, Address: trimmed);
    }

    private async Task<List<VoiceAnswerCandidate>> GetFoldersAsync(List<MailAccount> accounts, string text, CancellationToken cancellationToken)
    {
        var result = new List<VoiceAnswerCandidate>();
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folders = await folderService.GetFoldersAsync(account.Id).ConfigureAwait(false) ?? [];
            var byRemoteId = folders
                .Where(static folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
                .GroupBy(static folder => folder.RemoteFolderId, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

            var title = AccountTitle(account);
            result.AddRange(byRemoteId.Values
                .Where(static folder => !folder.IsHidden && !HiddenFolderTypes.Contains(folder.SpecialFolderType))
                .Select(folder => (Folder: folder, Parent: ParentPath(folder, byRemoteId)))
                .Where(entry => Matches(text, FolderName(entry.Folder), entry.Parent))
                .OrderBy(static entry => entry.Folder.SpecialFolderType == SpecialFolderType.Other ? 1 : 0)
                .ThenBy(static entry => entry.Parent, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static entry => FolderName(entry.Folder), StringComparer.CurrentCultureIgnoreCase)
                .Select(entry => new VoiceAnswerCandidate($"f:{entry.Folder.Id:N}", FolderName(entry.Folder), entry.Parent, title, account, Folder: entry.Folder)));
        }

        return result;
    }

    private async Task<List<VoiceAnswerCandidate>> GetCalendarsAsync(List<MailAccount> accounts, string text, CancellationToken cancellationToken)
    {
        var result = new List<VoiceAnswerCandidate>();
        foreach (var account in accounts.Where(static account => account.IsCalendarAccessGranted && account.IsCalendarAccessEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var calendars = await calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false) ?? [];
            var title = AccountTitle(account);
            result.AddRange(calendars
                .Where(static calendar => !calendar.IsReadOnly && !string.IsNullOrWhiteSpace(calendar.RemoteCalendarId))
                .Where(calendar => Matches(text, calendar.Name, null))
                .OrderByDescending(static calendar => calendar.IsPrimary)
                .ThenBy(static calendar => calendar.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(calendar => new VoiceAnswerCandidate($"c:{calendar.Id:N}", calendar.Name ?? string.Empty, null, title, account, Calendar: calendar)));
        }

        return result;
    }

    private static string? ParentPath(MailItemFolder folder, Dictionary<string, MailItemFolder> byRemoteId)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { folder.RemoteFolderId };
        var parentId = folder.ParentRemoteFolderId;
        while (!string.IsNullOrWhiteSpace(parentId) && seen.Add(parentId) && byRemoteId.TryGetValue(parentId, out var parent))
        {
            names.Insert(0, FolderName(parent));
            parentId = parent.ParentRemoteFolderId;
        }

        return names.Count == 0 ? null : string.Join(" / ", names);
    }

    private static string FolderName(MailItemFolder folder)
        => (string.IsNullOrWhiteSpace(folder.DisplayName) ? folder.FolderName : folder.DisplayName)?.Trim() ?? string.Empty;

    private static string Ask(UnresolvedKind kind, string phrase, bool isRelationship) => kind switch
    {
        UnresolvedKind.Person => string.Format(CultureInfo.CurrentCulture, isRelationship ? Translator.VoiceQuestion_PersonRelationship : Translator.VoiceQuestion_Person, phrase),
        UnresolvedKind.Account => string.Format(CultureInfo.CurrentCulture, Translator.VoiceQuestion_Account, phrase),
        UnresolvedKind.Folder => string.Format(CultureInfo.CurrentCulture, Translator.VoiceQuestion_Folder, phrase),
        UnresolvedKind.Sender => string.Format(CultureInfo.CurrentCulture, Translator.VoiceQuestion_Sender, phrase),
        UnresolvedKind.Group => string.Format(CultureInfo.CurrentCulture, Translator.VoiceQuestion_Group, phrase),
        UnresolvedKind.Filter => string.Format(CultureInfo.CurrentCulture, Translator.VoiceQuestion_Filter, phrase),
        UnresolvedKind.DefaultAccount => phrase == nameof(BrainDefaultUse.Task) ? Translator.VoiceQuestion_DefaultAccountTask : Translator.VoiceQuestion_DefaultAccountMail,
        _ => Translator.VoiceQuestion_DefaultCalendar
    };

    private static bool Matches(string filter, string? primary, string? secondary)
        => filter.Length == 0
           || Contains(primary, filter)
           || Contains(secondary, filter);

    private static bool Contains(string? text, string part)
        => !string.IsNullOrWhiteSpace(text)
           && CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, part, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private static bool SamePhrase(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private static string? Distinct(string? detail, string title)
        => string.IsNullOrWhiteSpace(detail) || string.Equals(detail, title, StringComparison.OrdinalIgnoreCase) ? null : detail;

    private static string AccountTitle(MailAccount account)
        => !string.IsNullOrWhiteSpace(account.Name) ? account.Name
            : !string.IsNullOrWhiteSpace(account.SenderName) ? account.SenderName
            : account.Address ?? string.Empty;
}
