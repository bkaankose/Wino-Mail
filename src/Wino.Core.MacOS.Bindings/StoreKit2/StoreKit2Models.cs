namespace Wino.Core.MacOS.Bindings.StoreKit2;

/// <summary>The App Store storefront of the signed-in Apple Account. <see cref="CountryCode"/> is ISO 3166-1 alpha-3, such as "USA".</summary>
public sealed record StoreKit2Storefront(string Id, string CountryCode);

public enum StoreKit2ProductType
{
    Unknown,
    Consumable,
    NonConsumable,
    AutoRenewable,
    NonRenewable
}

public enum StoreKit2PeriodUnit
{
    Unknown,
    Day,
    Week,
    Month,
    Year
}

public sealed record StoreKit2Product(
    string Id,
    StoreKit2ProductType Type,
    string DisplayName,
    string Description,
    string DisplayPrice,
    decimal Price,
    string? CurrencyCode,
    StoreKit2PeriodUnit? SubscriptionPeriodUnit,
    int? SubscriptionPeriodValue,
    string? SubscriptionGroupId,
    bool IsFamilyShareable);

/// <summary>
/// A StoreKit transaction. <see cref="Jws"/> is the signed transaction for server-side verification;
/// <see cref="IsVerified"/> is the on-device check only.
/// </summary>
public sealed record StoreKit2Transaction(
    ulong Id,
    ulong OriginalId,
    string ProductId,
    StoreKit2ProductType ProductType,
    DateTimeOffset PurchaseDate,
    DateTimeOffset OriginalPurchaseDate,
    DateTimeOffset? ExpirationDate,
    DateTimeOffset? RevocationDate,
    bool IsUpgraded,
    Guid? AppAccountToken,
    string Environment,
    string StorefrontCountryCode,
    string Jws,
    bool IsVerified,
    string? VerificationError);

public enum StoreKit2PurchaseStatus
{
    Success,
    /// <summary>Waiting for approval, such as Ask to Buy. The transaction arrives later through updates.</summary>
    Pending,
    UserCancelled
}

/// <summary><see cref="Transaction"/> is set only when <see cref="Status"/> is <see cref="StoreKit2PurchaseStatus.Success"/>.</summary>
public sealed record StoreKit2PurchaseResult(StoreKit2PurchaseStatus Status, StoreKit2Transaction? Transaction);

public sealed class StoreKit2Exception(string message) : Exception(message);
