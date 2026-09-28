#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Services;

public sealed class WinoStorePurchaseRedeemService(
    IDatabaseService databaseService,
    IWinoAccountApiClient apiClient,
    IMicrosoftStoreService storeService,
    IConfigurationService configuration) : IWinoStorePurchaseRedeemService
{
    private const string HiddenKeyPrefix = "StoreUnlimitedRedeemHidden_";
    private const string StoreUserIdClaim = "http://schemas.microsoft.com/marketplace/2015/08/claims/key/userId";

    /// <summary>
    /// Used when the Store ID key has no readable user ID, so the redeem card still works and can still be hidden.
    /// </summary>
    internal const string UnknownStoreUserHash = "unknown";

    private readonly ILogger _logger = Log.ForContext<WinoStorePurchaseRedeemService>();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<WinoStoreRedeemCandidate?> GetRedeemCandidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var account = await databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
            if (account is null || account.IsUnlimitedAccountsEnabled)
                return null;

            if (!await storeService.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS).ConfigureAwait(false))
                return null;

            // Only a signed-in account without the add-on and with a Store license gets this far.
            var ticket = await apiClient.CreateStoreCollectionsIdTicketAsync(cancellationToken).ConfigureAwait(false);
            if (!ticket.IsSuccess || ticket.Result is null)
            {
                _logger.Warning("Store service ticket was not issued. Error code: {ErrorCode}", ticket.ErrorCode);
                return null;
            }

            var storeIdKey = await storeService
                .GetCustomerCollectionsIdAsync(ticket.Result.ServiceTicket, ticket.Result.PublisherUserId)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(storeIdKey))
            {
                _logger.Warning("The Microsoft Store returned no Store ID key.");
                return null;
            }

            var storeUserHash = GetStoreUserHash(storeIdKey);
            if (IsRedeemHidden(storeUserHash))
                return null;

            return new WinoStoreRedeemCandidate(storeUserHash, storeIdKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Microsoft Store redeem candidate could not be created.");
            return null;
        }
    }

    public async Task<WinoStorePurchaseRedeemOutcome> RedeemUnlimitedAccountsAsync(WinoStoreRedeemCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // A second click while the first redeem runs sees the first one's result on the account.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var redeem = await apiClient.RedeemStoreUnlimitedAccountsAsync(candidate.StoreIdKey, cancellationToken).ConfigureAwait(false);
            if (redeem.IsSuccess && redeem.Result?.IsUnlimitedAccountsEnabled == true)
            {
                _logger.Information("Microsoft Store Unlimited Accounts purchase was linked to the Wino Account.");
                return WinoStorePurchaseRedeemOutcome.Redeemed;
            }

            _logger.Information("Microsoft Store purchase was not redeemed. Error code: {ErrorCode}", redeem.ErrorCode);
            var outcome = redeem.ErrorCode switch
            {
                ApiErrorCodes.MicrosoftStorePurchaseAlreadyLinked => WinoStorePurchaseRedeemOutcome.AlreadyLinked,
                ApiErrorCodes.MicrosoftStorePurchaseNotFound => WinoStorePurchaseRedeemOutcome.NotOwned,
                _ => WinoStorePurchaseRedeemOutcome.Failed
            };

            // Retrying cannot change these answers for this Store user, so stop offering the redeem.
            if (outcome is WinoStorePurchaseRedeemOutcome.AlreadyLinked or WinoStorePurchaseRedeemOutcome.NotOwned)
                configuration.Set(HiddenKeyPrefix + candidate.StoreUserHash, true);

            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Microsoft Store purchase redeem failed.");
            return WinoStorePurchaseRedeemOutcome.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsRedeemHidden(string storeUserHash)
        => configuration.Get(HiddenKeyPrefix + storeUserHash, false);

    /// <summary>
    /// Hashes the Store user ID claim of a Store ID key. The key is a JWT; the signature is not checked
    /// here because the server validates the key. Only the hash is kept, never the Store user ID.
    /// </summary>
    internal string GetStoreUserHash(string storeIdKey)
    {
        var userId = ReadStoreUserId(storeIdKey);
        if (string.IsNullOrEmpty(userId))
        {
            _logger.Warning("The Store ID key has no readable Store user ID claim.");
            return UnknownStoreUserHash;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();
    }

    private static string? ReadStoreUserId(string storeIdKey)
    {
        var segments = storeIdKey.Split('.');
        if (segments.Length < 2 || segments[1].Length == 0)
            return null;

        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(StoreUserIdClaim, out var claim) &&
                   claim.ValueKind == JsonValueKind.String
                ? claim.GetString()
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
