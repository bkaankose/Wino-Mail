using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.AI.Abstractions;
using Wino.Services;
using Wino.Core.Tests.Helpers;
using Xunit;

namespace Wino.Core.Tests.Services;

public class WinoAccountProfileServiceTests : IAsyncLifetime
{
    private readonly Mock<IWinoAccountApiClient> _apiClient = new();
    private InMemoryDatabaseService _databaseService = null!;
    private WinoAccountProfileService _service = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _service = new WinoAccountProfileService(_databaseService, _apiClient.Object);
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task LoginAsync_ShouldPersistSingleActiveAccount()
    {
        var authResult = CreateAuthResult("first@example.com");

        _apiClient
            .Setup(x => x.LoginAsync("first@example.com", "pw", default))
            .ReturnsAsync(WinoAccountApiResult<AuthResultDto>.Success(authResult));

        var result = await _service.LoginAsync("first@example.com", "pw");

        result.IsSuccess.Should().BeTrue();
        result.Account.Should().NotBeNull();

        var persisted = await _databaseService.Connection.Table<WinoAccount>().ToListAsync();
        persisted.Should().ContainSingle();
        persisted[0].Email.Should().Be("first@example.com");
        persisted[0].AccessToken.Should().Be(authResult.AccessToken);
        persisted[0].RefreshToken.Should().Be(authResult.RefreshToken);
    }

    [Fact]
    public async Task LoginAsync_ShouldReplaceExistingActiveAccount()
    {
        _apiClient
            .Setup(x => x.LoginAsync("first@example.com", "pw", default))
            .ReturnsAsync(WinoAccountApiResult<AuthResultDto>.Success(CreateAuthResult("first@example.com")));

        _apiClient
            .Setup(x => x.LoginAsync("second@example.com", "pw", default))
            .ReturnsAsync(WinoAccountApiResult<AuthResultDto>.Success(CreateAuthResult("second@example.com")));

        await _service.LoginAsync("first@example.com", "pw");
        await _service.LoginAsync("second@example.com", "pw");

        var persisted = await _databaseService.Connection.Table<WinoAccount>().ToListAsync();
        persisted.Should().ContainSingle();
        persisted[0].Email.Should().Be("second@example.com");
    }

    [Fact]
    public async Task SignOutAsync_ShouldDeletePersistedAccount()
    {
        var authResult = CreateAuthResult("signout@example.com");

        _apiClient
            .Setup(x => x.LoginAsync("signout@example.com", "pw", default))
            .ReturnsAsync(WinoAccountApiResult<AuthResultDto>.Success(authResult));

        _apiClient
            .Setup(x => x.LogoutAsync(authResult.RefreshToken, default))
            .ReturnsAsync(ApiEnvelope<System.Text.Json.JsonElement>.Success(default));

        await _service.LoginAsync("signout@example.com", "pw");
        await _service.SignOutAsync();

        var persisted = await _databaseService.Connection.Table<WinoAccount>().ToListAsync();
        persisted.Should().BeEmpty();
    }

    [Fact]
    public async Task LoginAsync_ShouldPreserveEnvelopeErrorMessage()
    {
        _apiClient
            .Setup(x => x.LoginAsync("first@example.com", "pw", default))
            .ReturnsAsync(WinoAccountApiResult<AuthResultDto>.Failure(ApiErrorCodes.InvalidCredentials, "Password does not match this account."));

        var result = await _service.LoginAsync("first@example.com", "pw");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ApiErrorCodes.InvalidCredentials);
        result.ErrorMessage.Should().Be("Password does not match this account.");
    }

    [Fact]
    public async Task LoginAsync_ShouldPreserveErrorDetails()
    {
        var details = JsonSerializer.SerializeToElement(new EmailConfirmationRequiredDetailsDto(
            "/api/v1/auth/confirm-email/resend",
            "ticket",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(8)));

        _apiClient
            .Setup(x => x.LoginAsync("first@example.com", "pw", default))
            .ReturnsAsync(WinoAccountApiResult<AuthResultDto>.Failure(ApiErrorCodes.EmailNotConfirmed, null, details));

        var result = await _service.LoginAsync("first@example.com", "pw");

        result.IsSuccess.Should().BeFalse();
        result.ErrorDetails.Should().NotBeNull();
        JsonSerializer.Deserialize<EmailConfirmationRequiredDetailsDto>(result.ErrorDetails!.Value.GetRawText())!
            .ResendConfirmationTicket.Should().Be("ticket");
    }

    [Fact]
    public async Task RegisterAsync_ShouldNotPersistAccountUntilEmailIsConfirmed()
    {
        var authResult = CreateAuthResult("register@example.com");

        _apiClient
            .Setup(x => x.RegisterAsync("register@example.com", "pw", default))
            .ReturnsAsync(WinoAccountApiResult<AuthResultDto>.Success(authResult));

        var result = await _service.RegisterAsync("register@example.com", "pw");

        result.IsSuccess.Should().BeTrue();
        result.Account.Should().NotBeNull();

        var persisted = await _databaseService.Connection.Table<WinoAccount>().ToListAsync();
        persisted.Should().BeEmpty();
    }

    [Fact]
    public async Task ForgotPasswordAsync_ShouldForwardApiResponse()
    {
        _apiClient
            .Setup(x => x.ForgotPasswordAsync("reset@example.com", default))
            .ReturnsAsync(ApiEnvelope<JsonElement>.Success(default));

        var result = await _service.ForgotPasswordAsync("reset@example.com");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshProfileAsync_ShouldPersistLatestProfileData()
    {
        var authResult = CreateAuthResult("first@example.com");

        _apiClient
            .Setup(x => x.LoginAsync("first@example.com", "pw", default))
            .ReturnsAsync(WinoAccountApiResult<AuthResultDto>.Success(authResult));

        _apiClient
            .Setup(x => x.GetCurrentUserAsync(default))
            .ReturnsAsync(ApiEnvelope<AuthUserDto>.Success(new AuthUserDto(
                authResult.User.UserId,
                "updated@example.com",
                "Premium",
                authResult.User.HasPassword,
                authResult.User.HasGoogleLogin,
                authResult.User.HasFacebookLogin,
                true)));

        await _service.LoginAsync("first@example.com", "pw");

        var result = await _service.RefreshProfileAsync();

        result.IsSuccess.Should().BeTrue();
        result.Account.Should().NotBeNull();
        result.Account!.Email.Should().Be("updated@example.com");
        result.Account.AccountStatus.Should().Be("Premium");
        result.Account.IsUnlimitedAccountsEnabled.Should().BeTrue();

        var persisted = await _databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync();
        persisted.Should().NotBeNull();
        persisted!.Email.Should().Be("updated@example.com");
        persisted.AccountStatus.Should().Be("Premium");
        persisted.IsUnlimitedAccountsEnabled.Should().BeTrue();
        persisted.AccessToken.Should().Be(authResult.AccessToken);
        persisted.RefreshToken.Should().Be(authResult.RefreshToken);
    }

    [Fact]
    public async Task SummarizeAsync_ShouldReturnAiResponse()
    {
        var authResult = CreateAuthResult("first@example.com");

        _apiClient
            .Setup(x => x.LoginAsync("first@example.com", "pw", default))
            .ReturnsAsync(WinoAccountApiResult<AuthResultDto>.Success(authResult));

        _apiClient
            .Setup(x => x.SummarizeAsync(It.IsAny<IReadOnlyList<MailContentSegment>>(), "en", default))
            .ReturnsAsync(ApiEnvelope<AiSummaryResultDto>.Success(new AiSummaryResultDto("Summary")));

        await _service.LoginAsync("first@example.com", "pw");

        var response = await _service.SummarizeAsync(
            [new MailContentSegment("s000001", MailContentSection.CurrentMessage, MailContentSegmentKind.Paragraph, "Hello")],
            "en");

        response.IsSuccess.Should().BeTrue();
        response.Result?.Text.Should().Be("Summary");
    }

    [Fact]
    public async Task RewriteAsync_ShouldPassCurrentApplicationLanguage()
    {
        var authResult = CreateAuthResult("rewrite@example.com");
        var translationService = new Mock<ITranslationService>();
        translationService.SetupGet(x => x.CurrentLanguageModel)
            .Returns(new AppLanguageModel(AppLanguage.Turkish, "Turkish", "tr-TR"));
        var localizedService = new WinoAccountProfileService(
            _databaseService,
            _apiClient.Object,
            translationService.Object);

        _apiClient
            .Setup(x => x.LoginAsync("rewrite@example.com", "pw", default))
            .ReturnsAsync(WinoAccountApiResult<AuthResultDto>.Success(authResult));
        _apiClient
            .Setup(x => x.RewriteAsync("<p>Hello</p>", "polite", "tr-TR", default))
            .ReturnsAsync(ApiEnvelope<AiTextResultDto>.Success(new AiTextResultDto("<p>Merhaba</p>")));

        await localizedService.LoginAsync("rewrite@example.com", "pw");
        var response = await localizedService.RewriteAsync("<p>Hello</p>", "polite");

        response.IsSuccess.Should().BeTrue();
        response.Result?.Html.Should().Be("<p>Merhaba</p>");
        _apiClient.Verify(x => x.RewriteAsync("<p>Hello</p>", "polite", "tr-TR", default), Times.Once);
    }

    private static AuthResultDto CreateAuthResult(string email)
    {
        return new AuthResultDto(
            new AuthUserDto(Guid.NewGuid(), email, "Active", true, false, false, false),
            "access-token",
            DateTimeOffset.UtcNow.AddMinutes(30),
            "refresh-token",
            DateTimeOffset.UtcNow.AddDays(30));
    }
}
