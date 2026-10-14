using System.Text;
using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Mail.Api.Contracts.Common;
using Wino.Services.PushRelay;
using Xunit;

namespace Wino.Core.Tests.Services.PushRelay;

public sealed class PushRelayTokenProviderTests
{
    private readonly SettableTimeProvider _clock = new();
    private readonly Mock<IWinoAccountApiClient> _api = new();
    private int _calls;

    public PushRelayTokenProviderTests()
    {
        _api.Setup(x => x.CreateRelayTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ApiEnvelope<RelayTokenDto>.Success(
                new RelayTokenDto(Jwt(7) + Interlocked.Increment(ref _calls), _clock.Now.AddMinutes(30), "https://relay.example.test")));
    }

    [Fact]
    public async Task GetTokenAsync_CachesUntilTwoMinutesBeforeExpiry()
    {
        var provider = new PushRelayTokenProvider(_api.Object, _clock);

        var first = await provider.GetTokenAsync();
        _clock.Advance(TimeSpan.FromMinutes(27));
        var cached = await provider.GetTokenAsync();
        _clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        var renewed = await provider.GetTokenAsync();

        cached.Should().BeSameAs(first);
        renewed.Token.Should().NotBe(first.Token);
        _calls.Should().Be(2);
    }

    [Fact]
    public async Task GetTokenAsync_ConcurrentCallersShareOneRequest()
    {
        var gate = new TaskCompletionSource<ApiEnvelope<RelayTokenDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _api.Setup(x => x.CreateRelayTokenAsync(It.IsAny<CancellationToken>()))
            .Returns(() => { Interlocked.Increment(ref calls); return gate.Task; });
        var provider = new PushRelayTokenProvider(_api.Object, _clock);

        var waiters = Enumerable.Range(0, 5).Select(_ => provider.GetTokenAsync()).ToArray();
        gate.SetResult(ApiEnvelope<RelayTokenDto>.Success(new RelayTokenDto("shared", _clock.Now.AddMinutes(30), null)));
        var tokens = await Task.WhenAll(waiters);

        calls.Should().Be(1);
        tokens.Should().OnlyContain(x => x.Token == "shared");
    }

    [Fact]
    public async Task Invalidate_ForcesANewRequest()
    {
        var provider = new PushRelayTokenProvider(_api.Object, _clock);

        var first = await provider.GetTokenAsync();
        provider.Invalidate();
        var second = await provider.GetTokenAsync();

        second.Token.Should().NotBe(first.Token);
        _calls.Should().Be(2);
    }

    [Fact]
    public async Task GetTokenAsync_UsesRelayUrlAsBaseAddressAndReadsMailboxLimit()
    {
        var provider = new PushRelayTokenProvider(_api.Object, _clock);

        var token = await provider.GetTokenAsync();

        token.RelayBaseUri.Should().Be(new Uri("https://relay.example.test/"));
        token.MailboxLimit.Should().Be(7);
        provider.MailboxLimit.Should().Be(7);
        token.ToString().Should().NotContain(token.Token);
    }

    [Fact]
    public async Task GetTokenAsync_WithoutRelayUrl_UsesDefault()
    {
        _api.Setup(x => x.CreateRelayTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiEnvelope<RelayTokenDto>.Success(new RelayTokenDto("t", _clock.Now.AddMinutes(30), null)));
        var provider = new PushRelayTokenProvider(_api.Object, _clock);

        (await provider.GetTokenAsync()).RelayBaseUri.Should().Be(new Uri(PushRelayDefaults.DefaultBaseUrl));
    }

    [Theory]
    [InlineData(RelayApiErrorCodes.RelayUnavailable, PushRelayErrorCodes.ServiceUnavailable)]
    [InlineData(WinoAccountClientErrorCodes.ServiceUnavailable, PushRelayErrorCodes.ServiceUnavailable)]
    [InlineData(WinoAccountClientErrorCodes.SignInRequired, PushRelayErrorCodes.SignInRequired)]
    [InlineData("HTTP 401 Unauthorized", PushRelayErrorCodes.SignInRequired)]
    public async Task GetTokenAsync_MapsApiFailures(string apiError, string expectedCode)
    {
        _api.Setup(x => x.CreateRelayTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiEnvelope<RelayTokenDto>.Failure(apiError));
        var provider = new PushRelayTokenProvider(_api.Object, _clock);

        var act = () => provider.GetTokenAsync();

        (await act.Should().ThrowAsync<PushRelayApiException>()).Which.Code.Should().Be(expectedCode);
    }

    private static string Jwt(int mailboxLimit)
    {
        static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Encode("{\"alg\":\"ES256\"}")}.{Encode($"{{\"sub\":\"u\",\"wr_mbx\":{mailboxLimit}}}")}.sig";
    }
}
