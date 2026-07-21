using System.Xml.Linq;
using FluentAssertions;
using Wino.AppServices.Contracts;

namespace Wino.AppServices.Tests;

public sealed class ManifestContractTests
{
    [Fact]
    public void UwpManifestUsesOneIdentityForMailCalendarAndCompanionExtensions()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "Wino.Mail.Uwp", "Package.appxmanifest"));
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        XNamespace desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
        XNamespace restricted = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

        var applications = document.Root!.Element(foundation + "Applications")!.Elements(foundation + "Application").ToList();
        applications.Select(application => (string?)application.Attribute("Id")).Should().Equal("App");

        var mail = applications.Single(application => (string?)application.Attribute("Id") == "App");
        mail.Descendants(uap + "AppService").Single().Attribute("Name")!.Value.Should().Be(AppServiceProtocol.ServiceName);
        mail.Descendants(desktop + "Extension").Select(extension => (string?)extension.Attribute("Category"))
            .Should().Contain(["windows.fullTrustProcess", "windows.startupTask"]);
        mail.Descendants(uap + "Protocol").Select(protocol => (string?)protocol.Attribute("Name"))
            .Should().Contain(["webcal", "webcals"]);
        mail.Descendants(uap + "FileType").Select(type => type.Value).Should().Contain(".ics");

        document.Descendants(restricted + "Capability").Select(capability => (string?)capability.Attribute("Name"))
            .Should().Contain("runFullTrust");

        document.Root!.Element(foundation + "Extensions")!
            .Descendants(foundation + "Folder")
            .Select(folder => (string?)folder.Attribute("Name"))
            .Should().ContainSingle()
            .Which.Should().Be("WinoShared");
    }

    [Fact]
    public void PackagingProjectOwnsUwpAndCompanionPayloads()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "Wino.Packaging", "Wino.Packaging.wapproj"));
        XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";

        project.Descendants(msbuild + "ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Should().Contain([
                "..\\Wino.Mail.Uwp\\Wino.Mail.Uwp.csproj",
                "..\\Wino.Companion\\Wino.Companion.csproj",
            ]);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinoMail.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate WinoMail.slnx.");
    }
}
