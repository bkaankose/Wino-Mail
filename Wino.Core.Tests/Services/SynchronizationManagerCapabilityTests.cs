using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
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

    [Fact]
    public void TryGetSslCertificateException_ReturnsTrue_WhenCertificateExceptionIsNested()
    {
        var certificateException = new ImapTestSSLCertificateException("issuer", "expires", "valid-from");
        var exception = new ImapClientPoolException(new InvalidOperationException("wrapped", certificateException));

        var found = SynchronizationManager.TryGetSslCertificateException(exception, out var result);

        found.Should().BeTrue();
        result.Should().BeSameAs(certificateException);
    }

    [Fact]
    public void TryGetSslCertificateException_ReturnsFalse_WhenNoCertificateExceptionExists()
    {
        var exception = new ImapClientPoolException(new InvalidOperationException("wrapped"));

        var found = SynchronizationManager.TryGetSslCertificateException(exception, out var result);

        found.Should().BeFalse();
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(SynchronizationCompletedState.Success, false)]
    [InlineData(SynchronizationCompletedState.Canceled, false)]
    [InlineData(SynchronizationCompletedState.Failed, true)]
    [InlineData(SynchronizationCompletedState.PartiallyCompleted, true)]
    public void ShouldTrackSynchronizationTelemetry_OnlyTracksFailures(
        SynchronizationCompletedState completedState,
        bool expected)
    {
        SynchronizationManager.ShouldTrackSynchronizationTelemetry(completedState).Should().Be(expected);
    }
}
