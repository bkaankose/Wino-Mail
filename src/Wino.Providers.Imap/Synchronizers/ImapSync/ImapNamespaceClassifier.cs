#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MailKit;
using MailKit.Net.Imap;
using Wino.Core.Domain.Enums;

namespace Wino.Providers.Imap.Synchronizers.ImapSync;

/// <summary>
/// Tells which namespace (RFC 2342) a mailbox name belongs to, by the longest namespace prefix it falls under.
/// INBOX is always personal. A name under no prefix belongs to the namespace with an empty prefix, personal first;
/// a server that announces no namespace at all is personal only.
/// </summary>
internal sealed class ImapNamespaceClassifier
{
    private readonly List<(string Prefix, char Separator, MailFolderScope Scope)> _prefixed;
    private readonly MailFolderScope _unprefixedScope;

    private ImapNamespaceClassifier(List<(string Prefix, char Separator, MailFolderScope Scope)> prefixed, MailFolderScope unprefixedScope)
    {
        _prefixed = prefixed;
        _unprefixedScope = unprefixedScope;
    }

    public bool HasNonPersonalNamespaces => _prefixed.Any(entry => entry.Scope != MailFolderScope.Personal) ||
                                            _unprefixedScope != MailFolderScope.Personal;

    public static ImapNamespaceClassifier Create(IImapClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var namespaces = client.PersonalNamespaces.Select(item => (item, MailFolderScope.Personal))
            .Concat(client.OtherNamespaces.Select(item => (item, MailFolderScope.OtherUser)))
            .Concat(client.SharedNamespaces.Select(item => (item, MailFolderScope.Shared)))
            .ToList();

        var prefixed = new List<(string Prefix, char Separator, MailFolderScope Scope)>();
        var unprefixed = new HashSet<MailFolderScope>();

        foreach (var (folderNamespace, scope) in namespaces)
        {
            var separator = folderNamespace.DirectorySeparator;
            var prefix = folderNamespace.Path ?? string.Empty;

            if (separator != '\0')
                prefix = prefix.TrimEnd(separator);

            if (prefix.Length == 0)
                unprefixed.Add(scope);
            else
                prefixed.Add((prefix, separator, scope));
        }

        var unprefixedScope = unprefixed.Contains(MailFolderScope.Personal) || unprefixed.Count == 0
            ? MailFolderScope.Personal
            : unprefixed.Contains(MailFolderScope.OtherUser) ? MailFolderScope.OtherUser : MailFolderScope.Shared;

        return new ImapNamespaceClassifier(prefixed.OrderByDescending(entry => entry.Prefix.Length).ToList(), unprefixedScope);
    }

    public MailFolderScope Classify(IMailFolder folder) => Classify(folder.FullName);

    public MailFolderScope Classify(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName) || string.Equals(fullName, "INBOX", StringComparison.OrdinalIgnoreCase))
            return MailFolderScope.Personal;

        foreach (var (prefix, separator, scope) in _prefixed)
        {
            if (string.Equals(fullName, prefix, StringComparison.Ordinal))
                return scope;

            if (fullName.Length > prefix.Length &&
                fullName.StartsWith(prefix, StringComparison.Ordinal) &&
                (separator == '\0' || fullName[prefix.Length] == separator))
            {
                return scope;
            }
        }

        return _unprefixedScope;
    }
}
