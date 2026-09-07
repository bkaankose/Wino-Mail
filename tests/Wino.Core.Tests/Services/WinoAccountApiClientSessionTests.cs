using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoAccountApiClientSessionTests
{
    [Fact]
    public async Task UnauthorizedWithUnexpiredToken_RefreshesAndRetriesWithRotatedToken()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        var accountId = Guid.NewGuid();
        await database.Connection.InsertAsync(new WinoAccount
        {
            Id = accountId, AccessToken = "rejected", RefreshToken = "refresh",
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        using var handler = new RefreshHandler(accountId);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        using var client = new WinoAccountApiClient(database, http);

        var response = await client.GetCurrentUserAsync();

        response.IsSuccess.Should().BeTrue();
        handler.RefreshRequests.Should().Be(1);
        handler.Bearers.Should().Equal("rejected", "rotated");
        (await database.Connection.Table<WinoAccount>().FirstAsync()).RefreshToken.Should().Be("new-refresh");
    }

    private sealed class RefreshHandler(Guid accountId) : HttpMessageHandler
    {
        public int RefreshRequests { get; private set; }
        public List<string?> Bearers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object user = new { userId = accountId, email = "test@example.test", accountStatus = "Active" };
            object result;
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/refresh")
            {
                RefreshRequests++;
                result = new
                {
                    user, accessToken = "rotated", refreshToken = "new-refresh",
                    accessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                    refreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1)
                };
            }
            else
            {
                Bearers.Add(request.Headers.Authorization?.Parameter);
                if (request.Headers.Authorization?.Parameter == "rejected")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                result = user;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { isSuccess = true, result }), Encoding.UTF8, "application/json")
            });
        }
    }
}
