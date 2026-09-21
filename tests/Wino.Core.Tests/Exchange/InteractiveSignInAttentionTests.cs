using System;
using Wino.Authentication.Exchange;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Services;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Exchange;

/// <summary>
/// A rejected refresh token surfaces as an ordinary failed result. The manager has to recognise it
/// and mark the account, or the pass is repeated, and fails, on every timer tick.
/// </summary>
public class InteractiveSignInAttentionTests
{
    private readonly MailAccount _account = new() { Id = Guid.NewGuid() };

    [Fact]
    public void ARejectedSignIn_BecomesTheAttentionException_AndKeepsItsReason()
    {
        var rejected = new ExchangeInteractiveSignInRequiredException("the refresh token was rejected");

        var act = () => SynchronizationManager.ThrowIfInteractiveSignInRequired(_account, rejected);

        act.Should().Throw<AuthenticationAttentionException>()
            .Where(e => e.Account == _account && e.Message == rejected.Message && e.InnerException == rejected);
    }

    [Fact]
    public void ARejectedSignIn_IsFound_WhenAnotherExceptionWrapsIt()
    {
        var wrapped = new InvalidOperationException("session could not be opened",
            new ExchangeInteractiveSignInRequiredException("the refresh token was rejected"));

        var act = () => SynchronizationManager.ThrowIfInteractiveSignInRequired(_account, wrapped);

        act.Should().Throw<AuthenticationAttentionException>().WithMessage("the refresh token was rejected");
    }

    [Fact]
    public void AnyOtherFailure_AndNoFailure_AreLeftAlone()
    {
        var other = () => SynchronizationManager.ThrowIfInteractiveSignInRequired(_account, new TimeoutException());
        var none = () => SynchronizationManager.ThrowIfInteractiveSignInRequired(_account, null);

        other.Should().NotThrow();
        none.Should().NotThrow();
    }
}
