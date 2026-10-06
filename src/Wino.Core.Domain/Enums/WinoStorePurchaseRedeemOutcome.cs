namespace Wino.Core.Domain.Enums;

public enum WinoStorePurchaseRedeemOutcome
{
    /// <summary>Nothing to redeem: signed out, already unlocked, or no Store license on this device.</summary>
    NotNeeded,

    /// <summary>The Store purchase is now linked to the Wino Account.</summary>
    Redeemed,

    /// <summary>The Store purchase is already linked to a different Wino Account.</summary>
    AlreadyLinked,

    /// <summary>The Store did not report an active purchase for the Store user.</summary>
    NotOwned,

    /// <summary>The Store or the Wino Account API could not be reached, or returned an error.</summary>
    Failed,

    /// <summary>This platform cannot access Microsoft Store purchases; account entitlements remain valid.</summary>
    Unavailable
}
