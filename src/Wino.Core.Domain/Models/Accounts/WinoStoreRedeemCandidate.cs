namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// A Microsoft Store Unlimited Accounts purchase on this device that the signed-in Wino Account
/// can redeem.
/// </summary>
/// <param name="StoreUserHash">SHA-256 hex of the Store user ID in the Store ID key. Identifies the Store user without storing the ID.</param>
/// <param name="StoreIdKey">The Microsoft Store ID key that the Wino Account API uses to check the purchase.</param>
public sealed record WinoStoreRedeemCandidate(string StoreUserHash, string StoreIdKey);
