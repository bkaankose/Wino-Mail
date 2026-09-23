using FluentAssertions;
using Wino.Core.Domain.Enums;
using Xunit;

namespace Wino.Core.Tests;

public sealed class SearchModePreferenceTests
{
    [Theory]
    [InlineData("Local", SearchMode.Local)]
    [InlineData("0", SearchMode.Local)]
    [InlineData("Online", SearchMode.Online)]
    [InlineData("1", SearchMode.Online)]
    [InlineData("Semantic", SearchMode.Local)]
    [InlineData("2", SearchMode.Local)]
    [InlineData("unknown", SearchMode.Local)]
    [InlineData(null, SearchMode.Local)]
    public void Parse_NormalizesUnsupportedValues(string? stored, SearchMode expected)
        => SearchModePreference.Parse(stored).Should().Be(expected);
}
