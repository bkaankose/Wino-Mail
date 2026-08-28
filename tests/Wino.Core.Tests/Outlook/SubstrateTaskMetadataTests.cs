using System.Text.Json;
using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Outlook;
using Xunit;

namespace Wino.Core.Tests.Outlook;

public class SubstrateTaskMetadataTests
{
    [Theory]
    [InlineData("dark_red")]
    [InlineData("DARK_RED")]
    [InlineData(" dark_red ")]
    public void TryGetColorHex_MapsKnownThemeColorsRegardlessOfCasingOrPadding(string themeColor)
    {
        SubstrateTaskMetadata.TryGetColorHex(themeColor, out var hex).Should().BeTrue();
        hex.Should().Be("#C42B1C");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mountain")]
    public void TryGetColorHex_LeavesUnknownThemeColorsAlone(string? themeColor)
    {
        // An unmapped name must not produce a colour, so the caller keeps the one it already has
        // rather than repainting the list with a guess.
        SubstrateTaskMetadata.TryGetColorHex(themeColor, out var hex).Should().BeFalse();
        hex.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(99)]
    public void TryGetSortKind_RefusesUnconfirmedSortTypes(int? sortType)
    {
        // SortType is undocumented. Until a value is confirmed against a real mailbox it must not
        // map, because a wrong guess silently reorders the user's list.
        SubstrateTaskMetadata.TryGetSortKind(sortType, out _).Should().BeFalse();
    }
}

public class SubstrateTaskPayloadTests
{
    // Trimmed from a real mailbox response.
    private const string TaskFoldersPayload = """
    {
      "DeltaLink": "https://outlook.office.com/todob2/api/v1/taskfolders?deltatoken=abc",
      "Value": [
        {
          "ChangeKey": "nMdP0zg8wkS+Ib2Eeh80LAAJLsUFSg==",
          "Id": "AQMkADAwATMwMAItYzc4Ni1mMzE4LTAwAi0wMAoALgAAAA==",
          "IsDefaultFolder": true,
          "Name": "Tasks",
          "SortType": 0,
          "SortAscending": true,
          "ShowCompletedTasks": true,
          "ThemeColor": "dark_red",
          "ParentFolderGroupId": null
        },
        {
          "ChangeKey": "nMdP0zg8wkS+Ib2Eeh80LAAJNuc07A==",
          "Id": "AQMkADAwATMwMAItYzc4Ni1mMzE4LTAwAi0wMAoALgAAAB==",
          "IsDefaultFolder": false,
          "Name": "Changelog 1.6.7",
          "SortType": 0,
          "SortAscending": false,
          "ShowCompletedTasks": true,
          "ThemeColor": "dark_blue",
          "ParentFolderGroupId": "RgAAAAC-TbggESVJRIpMeUxmvrp9BwAA0"
        }
      ]
    }
    """;

    private const string FolderGroupsPayload = """
    {
      "DeltaLink": "https://outlook.office.com/todob2/api/v1/foldergroups?deltaToken=abc",
      "Value": [
        { "Id": "RgAAAAC-TbggESVJRIpMeUxmvrp9BwAA0", "OrderDateTime": "2026-07-31T21:27:56Z", "Name": "Wino" }
      ]
    }
    """;

    [Fact]
    public void TaskFolders_DeserializeWithParentGroupAndPresentationState()
    {
        var collection = JsonSerializer.Deserialize<SubstrateCollection<SubstrateTaskFolder>>(
            TaskFoldersPayload, SerializerOptions());

        collection.Should().NotBeNull();
        collection.DeltaLink.Should().Contain("deltatoken=abc");
        collection.Value.Should().HaveCount(2);

        var grouped = collection.Value[1];
        grouped.Name.Should().Be("Changelog 1.6.7");
        grouped.ParentFolderGroupId.Should().Be("RgAAAAC-TbggESVJRIpMeUxmvrp9BwAA0");
        grouped.SortAscending.Should().BeFalse();
        grouped.ShowCompletedTasks.Should().BeTrue();
        grouped.ThemeColor.Should().Be("dark_blue");

        collection.Value[0].ParentFolderGroupId.Should().BeNull();
        collection.Value[0].IsDefaultFolder.Should().BeTrue();
    }

    [Fact]
    public void FolderGroups_DeserializeIntoIdsThatTaskFoldersReference()
    {
        var groups = JsonSerializer.Deserialize<SubstrateCollection<SubstrateFolderGroup>>(
            FolderGroupsPayload, SerializerOptions());
        var folders = JsonSerializer.Deserialize<SubstrateCollection<SubstrateTaskFolder>>(
            TaskFoldersPayload, SerializerOptions());

        // The whole design rests on this join working without any id translation.
        groups.Value.Should().ContainSingle()
            .Which.Id.Should().Be(folders.Value[1].ParentFolderGroupId);
    }

    private static JsonSerializerOptions SerializerOptions() => new() { PropertyNameCaseInsensitive = true };
}
