namespace Wino.Core.Domain.Enums;

public enum WinoAppStorePurchaseOutcome
{
    /// <summary>Bought and unlocked. When a Wino Account is signed in, the purchase is linked to it.</summary>
    Purchased,

    /// <summary>Bought, but the Wino Account API could not link it yet. Wino retries on the next launch.</summary>
    PurchasedLinkPending,

    /// <summary>Waiting for approval, such as Ask to Buy.</summary>
    Pending,
    Cancelled,

    /// <summary>The product needs a signed-in Wino Account.</summary>
    SignInRequired,
    Failed
}
