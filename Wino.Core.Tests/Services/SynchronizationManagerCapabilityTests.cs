using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class SynchronizationManagerCapabilityTests
{
    [Fact]
    public void CanSynchronizeCalendar_ReturnsFalse_WhenCalendarAccessIsNotGranted()
    {
        var account = new MailAccount { IsCalendarAccessGranted = false };

        SynchronizationManager.CanSynchronizeCalendar(account).Should().BeFalse();
    }

    [Fact]
    public void CanSynchronizeCalendar_ReturnsTrue_WhenCalendarAccessIsGranted()
    {
        var account = new MailAccount { IsCalendarAccessGranted = true };

        SynchronizationManager.CanSynchronizeCalendar(account).Should().BeTrue();
    }

    [Fact]
    public void RequiresSynchronizerRefresh_ReturnsTrue_WhenCapabilitiesChanged()
    {
        var cachedAccount = new MailAccount
        {
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = false
        };
        var currentAccount = new MailAccount
        {
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = true
        };

        SynchronizationManager.RequiresSynchronizerRefresh(cachedAccount, currentAccount).Should().BeTrue();
    }

    [Fact]
    public void RequiresSynchronizerRefresh_ReturnsFalse_WhenCapabilitiesMatch()
    {
        var cachedAccount = new MailAccount
        {
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = false
        };
        var currentAccount = new MailAccount
        {
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = false
        };

        SynchronizationManager.RequiresSynchronizerRefresh(cachedAccount, currentAccount).Should().BeFalse();
    }
}
