using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.PublicFolders;
using Xunit;

namespace Wino.Core.Tests.PublicFolders;

public class PublicFolderClassifierTests
{
    [Theory]
    [InlineData("IPF.Note", PublicFolderKind.Mail)]
    [InlineData("IPF.Note.Microsoft.Approval", PublicFolderKind.Mail)]
    [InlineData("IPF.Appointment", PublicFolderKind.Calendar)]
    [InlineData("ipf.appointment", PublicFolderKind.Calendar)]
    [InlineData("IPF.Contact", PublicFolderKind.Contacts)]
    [InlineData("IPF.Contact.MOC.QuickContacts", PublicFolderKind.Contacts)]
    [InlineData("IPF.Task", PublicFolderKind.Other)]
    [InlineData("IPF.StickyNote", PublicFolderKind.Other)]
    public void Classify_MapsKnownContainerClasses(string folderClass, PublicFolderKind expected)
        => PublicFolderClassifier.Classify(folderClass).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_TreatsMissingClassAsContainer(string folderClass)
        => PublicFolderClassifier.Classify(folderClass).Should().Be(PublicFolderKind.Container);
}
