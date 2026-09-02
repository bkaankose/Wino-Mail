using FluentAssertions;
using MailKit;
using MailKit.Net.Imap;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Services;
using Xunit;
using IMailService = Wino.Core.Domain.Interfaces.IMailService;

namespace Wino.Core.Tests.Synchronizers;

public class KnownImapFolderResolverTests
{
    [Fact]
    public void ResolveKnownFolders_RecoversPartialICloudRolesFromProviderAliases()
    {
        var folders = new[]
        {
            Folder("INBOX", FolderAttributes.Inbox),
            Folder("Sent Messages", FolderAttributes.Sent),
            Folder("Deleted Messages", FolderAttributes.Trash),
            Folder("Drafts"),
            Folder("Junk"),
            Folder("Archive")
        };
        var client = Client(folders[0]);
        var account = Account(SpecialImapProvider.iCloud, "person@icloud.com", "imap.mail.me.com");

        var result = CreateSut().ResolveKnownFolders(client.Object, account, folders, []);

        result.Should().Contain(new Dictionary<string, SpecialFolderType>
        {
            ["INBOX"] = SpecialFolderType.Inbox,
            ["Sent Messages"] = SpecialFolderType.Sent,
            ["Deleted Messages"] = SpecialFolderType.Deleted,
            ["Drafts"] = SpecialFolderType.Draft,
            ["Junk"] = SpecialFolderType.Junk,
            ["Archive"] = SpecialFolderType.Archive
        });
    }

    [Fact]
    public void ResolveKnownFolders_RecoversYahooAliases()
    {
        var folders = new[] { Folder("Draft"), Folder("Bulk"), Folder("Archive") };

        var result = CreateSut().ResolveKnownFolders(
            Client().Object,
            Account(SpecialImapProvider.Yahoo, "person@yahoo.com", "imap.mail.yahoo.com"),
            folders,
            []);

        result["Draft"].Should().Be(SpecialFolderType.Draft);
        result["Bulk"].Should().Be(SpecialFolderType.Junk);
        result["Archive"].Should().Be(SpecialFolderType.Archive);
    }

    [Fact]
    public void ResolveKnownFolders_ServerAttributeOverridesConflictingAliasWithoutCapabilities()
    {
        var folder = Folder("Draft", FolderAttributes.Sent);
        var client = Client();
        client.SetupGet(value => value.Capabilities).Returns(ImapCapabilities.None);

        var result = CreateSut().ResolveKnownFolders(
            client.Object,
            Account(SpecialImapProvider.Yahoo, "person@yahoo.com", "imap.mail.yahoo.com"),
            [folder],
            []);

        result["Draft"].Should().Be(SpecialFolderType.Sent);
        result.Values.Should().NotContain(SpecialFolderType.Draft);
    }

    [Fact]
    public void ResolveKnownFolders_UsesSpecialFolderReferenceWithoutCapabilities()
    {
        var sent = Folder("Provider Sent");
        var client = Client();
        client.Setup(value => value.GetFolder(SpecialFolder.Sent)).Returns(sent);

        var result = CreateSut().ResolveKnownFolders(
            client.Object,
            Account(SpecialImapProvider.None, "person@example.test", "imap.example.test"),
            [sent],
            []);

        result["Provider Sent"].Should().Be(SpecialFolderType.Sent);
    }

    [Fact]
    public void ResolveKnownFolders_GenericAliasesRejectNestedFolders()
    {
        var namespaceFolder = Folder(string.Empty, isNamespace: true);
        var parent = Folder("Parent", parent: namespaceFolder);
        var rootSent = Folder("Sent", parent: namespaceFolder);
        var nestedDrafts = Folder("Parent/Drafts", name: "Drafts", parent: parent);

        var result = CreateSut().ResolveKnownFolders(
            Client().Object,
            Account(SpecialImapProvider.None, "person@example.test", "imap.example.test"),
            [rootSent, nestedDrafts],
            []);

        result["Sent"].Should().Be(SpecialFolderType.Sent);
        result.Should().NotContainKey("Parent/Drafts");
    }

    [Fact]
    public void ResolveKnownFolders_ProviderCanMatchAnExactFullPath()
    {
        var parent = Folder("Root");
        var nested = Folder("Root/Localized Drafts", name: "Localized Drafts", parent: parent);
        var catalog = new KnownImapProviderCatalog(new KnownImapProviderCatalogDocument
        {
            SchemaVersion = 1,
            Providers =
            [
                new KnownImapProviderDefinition
                {
                    Id = "full-path-test",
                    SpecialImapProvider = SpecialImapProvider.iCloud,
                    EmailDomains = ["full-path.test"],
                    IncomingHosts = ["imap.full-path.test"],
                    Incoming = Server("imap.full-path.test", 993),
                    Outgoing = Server("smtp.full-path.test", 587),
                    FolderAliases =
                    [
                        new KnownImapFolderAlias
                        {
                            Role = SpecialFolderType.Draft,
                            Value = "Root/Localized Drafts",
                            MatchFullPath = true
                        }
                    ]
                }
            ]
        });

        var result = CreateSut(catalog).ResolveKnownFolders(
            Client().Object,
            Account(SpecialImapProvider.None, "person@full-path.test", "imap.full-path.test"),
            [nested],
            []);

        result["Root/Localized Drafts"].Should().Be(SpecialFolderType.Draft);
    }

    [Fact]
    public void ResolveKnownFolders_AmbiguityUsesInterruptedBootstrapResult()
    {
        var first = Folder("Sent");
        var second = Folder("Sent Mail");
        var retained = new MailItemFolder { RemoteFolderId = "Sent Mail", SpecialFolderType = SpecialFolderType.Sent };

        var result = CreateSut().ResolveKnownFolders(
            Client().Object,
            Account(SpecialImapProvider.None, "person@example.test", "imap.example.test"),
            [first, second],
            [retained]);

        result.Should().ContainSingle(pair => pair.Value == SpecialFolderType.Sent);
        result["Sent Mail"].Should().Be(SpecialFolderType.Sent);
    }

    [Fact]
    public void ResolveKnownFolders_AmbiguityWithoutPriorResultLeavesRoleUnresolved()
    {
        var result = CreateSut().ResolveKnownFolders(
            Client().Object,
            Account(SpecialImapProvider.None, "person@example.test", "imap.example.test"),
            [Folder("Sent"), Folder("Sent Mail")],
            []);

        result.Values.Should().NotContain(SpecialFolderType.Sent);
    }

    [Fact]
    public void ResolveKnownFolders_UsesFixedMultiAttributePriorityAndIgnoresAll()
    {
        var multiple = Folder("Multiple", FolderAttributes.Junk | FolderAttributes.Drafts | FolderAttributes.Sent);
        var all = Folder("All Mail", FolderAttributes.All);

        var result = CreateSut().ResolveKnownFolders(
            Client().Object,
            Account(SpecialImapProvider.None, "person@example.test", "imap.example.test"),
            [multiple, all],
            []);

        result["Multiple"].Should().Be(SpecialFolderType.Draft);
        result.Should().NotContainKey("All Mail");
    }

    [Fact]
    public void ResolveKnownFolders_CompletedAccountDisablesAliasesButKeepsRfcRoles()
    {
        var account = Account(SpecialImapProvider.iCloud, "person@icloud.com", "imap.mail.me.com");
        account.ImapKnownFolderBootstrapState = ImapKnownFolderBootstrapState.Completed;

        var result = CreateSut().ResolveKnownFolders(
            Client().Object,
            account,
            [Folder("Drafts"), Folder("Server Sent", FolderAttributes.Sent)],
            []);

        result.Should().NotContainKey("Drafts");
        result["Server Sent"].Should().Be(SpecialFolderType.Sent);
    }

    private static UnifiedImapSynchronizer CreateSut(IKnownImapProviderCatalog catalog = null)
        => new(
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IImapSynchronizerErrorHandlerFactory>(),
            knownImapProviderCatalog: catalog ?? new EmbeddedKnownImapProviderCatalog(new KnownImapProviderCatalogLoader()));

    private static KnownImapServerDefinition Server(string host, int port)
        => new()
        {
            Host = host,
            Port = port,
            Security = ImapConnectionSecurity.Auto,
            Authentication = ImapAuthenticationMethod.Auto,
            UsernamePolicy = ImapUsernamePolicy.FullAddress
        };

    private static MailAccount Account(SpecialImapProvider provider, string address, string host)
        => new()
        {
            Address = address,
            SpecialImapProvider = provider,
            ImapKnownFolderBootstrapState = ImapKnownFolderBootstrapState.Pending,
            ServerInformation = new CustomServerInformation { IncomingServer = host }
        };

    private static Mock<IImapClient> Client(IMailFolder inbox = null)
    {
        var client = new Mock<IImapClient>();
        client.SetupGet(value => value.Inbox).Returns(inbox);
        client.SetupGet(value => value.Capabilities).Returns(ImapCapabilities.None);
        client.Setup(value => value.GetFolder(It.IsAny<SpecialFolder>())).Returns((IMailFolder)null);
        return client;
    }

    private static IMailFolder Folder(
        string fullName,
        FolderAttributes attributes = FolderAttributes.None,
        string name = null,
        IMailFolder parent = null,
        bool isNamespace = false)
    {
        var folder = new Mock<IMailFolder>();
        folder.SetupGet(value => value.FullName).Returns(fullName);
        folder.SetupGet(value => value.Name).Returns(name ?? fullName);
        folder.SetupGet(value => value.Attributes).Returns(attributes);
        folder.SetupGet(value => value.ParentFolder).Returns(parent);
        folder.SetupGet(value => value.IsNamespace).Returns(isNamespace);
        folder.SetupGet(value => value.Exists).Returns(true);
        folder.SetupGet(value => value.CanOpen).Returns(true);
        return folder.Object;
    }
}
