using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Contacts;

public enum RecipientSuggestionSource
{
    /// <summary>A saved or synchronized contact card.</summary>
    Contact,

    /// <summary>Someone the account corresponded with who is not a contact.</summary>
    History,

    /// <summary>A local contact list that expands into its members.</summary>
    List
}

/// <summary>
/// One row in the compose recipient picker. It is an <see cref="AccountContact"/> so the
/// recipient box can take it as a token directly; it is never written to the database.
/// </summary>
public sealed class RecipientSuggestion : AccountContact
{
    public RecipientSuggestionSource Source { get; init; }

    /// <summary>Where the suggestion comes from: an address book name, "From your mail", or a list's member count.</summary>
    public string SourceLabel { get; init; }

    /// <summary>The text the user typed, used to highlight the match.</summary>
    public string MatchQuery { get; init; }

    public double Score { get; init; }

    /// <summary>The contact card this suggestion was built from, if any.</summary>
    public Guid? ContactCardId { get; init; }

    /// <summary>For <see cref="RecipientSuggestionSource.List"/>, the members that have an address.</summary>
    public IReadOnlyList<AccountContact> ListMembers { get; init; } = [];

    public bool IsList => Source == RecipientSuggestionSource.List;

    /// <summary>Only remembered correspondents can be hidden; contacts are managed on the Contacts page.</summary>
    public bool CanSuppress => Source == RecipientSuggestionSource.History;

    /// <summary>The contact whose picture the row shows. Public so templates can bind it.</summary>
    public AccountContact PreviewContact => this;

    /// <summary>The line under the name: the address, or a list's member count.</summary>
    public string SecondaryText => IsList ? SourceLabel : Address;

    /// <summary>The small source caption; a list already shows it as its secondary text.</summary>
    public string SourceCaption => IsList ? null : SourceLabel;

    public static RecipientSuggestion FromContact(AccountContact card, string address, string sourceLabel, string matchQuery, double score)
    {
        var suggestion = new RecipientSuggestion
        {
            Source = RecipientSuggestionSource.Contact,
            SourceLabel = sourceLabel,
            MatchQuery = matchQuery,
            Score = score,
            ContactCardId = card.Id,
            MailAccountId = card.MailAccountId,
            AddressBookId = card.AddressBookId,
            SourceKind = card.SourceKind,
            DisplayName = card.DisplayValue,
            GivenName = card.GivenName,
            Surname = card.Surname,
            CompanyName = card.CompanyName,
            ContactPictureFileId = card.ContactPictureFileId,
            IsFavorite = card.IsFavorite
        };

        suggestion.Address = address;
        return suggestion;
    }

    public static RecipientSuggestion FromHistory(RecipientHistory history, string sourceLabel, string matchQuery, double score)
    {
        var suggestion = new RecipientSuggestion
        {
            Source = RecipientSuggestionSource.History,
            SourceLabel = sourceLabel,
            MatchQuery = matchQuery,
            Score = score,
            MailAccountId = history.AccountId,
            DisplayName = string.IsNullOrWhiteSpace(history.DisplayName) ? history.Address : history.DisplayName,
            IsAutoCollected = true
        };

        suggestion.Address = history.Address;
        return suggestion;
    }

    public static RecipientSuggestion FromList(ContactListRecipient list, string matchQuery, double score)
        => new()
        {
            Source = RecipientSuggestionSource.List,
            SourceLabel = list.Address,
            MatchQuery = matchQuery,
            Score = score,
            DisplayName = list.DisplayName,
            ListMembers = [.. list.ExpandRecipients()]
        };

    /// <summary>
    /// A plain recipient for an address the user typed, carrying the contact's name when one is known.
    /// </summary>
    public static AccountContact ForTypedAddress(string address, AccountContact knownContact)
    {
        if (knownContact is null)
            return new AccountContact { Name = address, Address = address };

        return FromContact(knownContact, address, sourceLabel: null, matchQuery: null, score: 0);
    }
}
