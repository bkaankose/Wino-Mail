#if WINDOWS
using FluentAssertions;
using Wino.Mail.Controls.IntelligenceProgressRing;
using Wino.Mail.Controls.MailListView;
using Wino.Mail.Controls.SearchBar;
using Wino.Mail.Controls.Shimmer;
using Xunit;

namespace Wino.Mail.Controls.Tests;

public sealed class ControlLifetimeContractTests
{
    [Theory]
    [InlineData(typeof(WinoMailListView))]
    [InlineData(typeof(WinoSearchBar))]
    [InlineData(typeof(WinoShimmer))]
    [InlineData(typeof(WinoIntelligenceProgressRing))]
    public void ResourceOwningControls_ExposeTerminalDisposal(Type controlType)
    {
        controlType.Should().BeAssignableTo<IDisposable>();
    }
}
#endif
