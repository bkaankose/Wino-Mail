using System.Net;
using Google;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Synchronizers.Errors.Gmail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class GmailAuthenticationFailureClassificationTests
{
    [Fact]
    public void IsAuthenticationFailure_InsufficientScopes_RequiresSignIn()
    {
        // Google's response when the stored token lacks a scope, e.g. calendar list with a mail-only grant.
        var exception = CreateException(HttpStatusCode.Forbidden, "Request had insufficient authentication scopes.", "insufficientPermissions");

        Assert.True(GmailAuthenticationFailedHandler.IsAuthenticationFailure(exception));
    }

    [Fact]
    public void IsAuthenticationFailure_Unauthorized_RequiresSignIn()
        => Assert.True(GmailAuthenticationFailedHandler.IsAuthenticationFailure(
            CreateException(HttpStatusCode.Unauthorized, "Request had invalid authentication credentials.", "authError")));

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "The user does not have sufficient permissions for file x.", "insufficientFilePermissions")]
    [InlineData(HttpStatusCode.Forbidden, "Quota exceeded for quota metric 'Queries'.", "rateLimitExceeded")]
    [InlineData(HttpStatusCode.Forbidden, "You need to have writer access to this calendar.", "requiredAccessLevel")]
    [InlineData(HttpStatusCode.TooManyRequests, "User-rate limit exceeded.", "rateLimitExceeded")]
    [InlineData(HttpStatusCode.NotFound, "Not Found", "notFound")]
    public void IsAuthenticationFailure_OtherProviderErrors_DoNotBlockTheAccount(HttpStatusCode status, string message, string reason)
        => Assert.False(GmailAuthenticationFailedHandler.IsAuthenticationFailure(CreateException(status, message, reason)));

    [Fact]
    public void IsAuthenticationFailure_NonGoogleException_IsFalse()
        => Assert.False(GmailAuthenticationFailedHandler.IsAuthenticationFailure(new InvalidOperationException("insufficient authentication")));

    [Fact]
    public void FromException_AuthenticationAttention_IsClassifiedAsAuthentication()
    {
        var issue = SynchronizationIssue.FromException(
            new AuthenticationAttentionException(new MailAccount { Id = Guid.NewGuid() }),
            "MailSync",
            SynchronizerErrorSeverity.AuthRequired);

        Assert.Equal(SynchronizerErrorCategory.Authentication, issue.Category);
        Assert.Equal(SynchronizerErrorSeverity.AuthRequired, issue.Severity);
    }

    [Fact]
    public void FromException_ExplicitCategory_IsKept()
    {
        var issue = SynchronizationIssue.FromException(
            new AuthenticationAttentionException(new MailAccount { Id = Guid.NewGuid() }),
            category: SynchronizerErrorCategory.Network);

        Assert.Equal(SynchronizerErrorCategory.Network, issue.Category);
    }

    [Fact]
    public void FromException_OtherExceptions_StayUnknown()
        => Assert.Equal(SynchronizerErrorCategory.Unknown, SynchronizationIssue.FromException(new InvalidOperationException()).Category);

    private static GoogleApiException CreateException(HttpStatusCode status, string message, string reason)
        => new(status, new GoogleApiError
        {
            Code = (int)status,
            Message = message,
            Errors = [new GoogleApiErrorDetail { Message = message, Reason = reason }]
        });
}
