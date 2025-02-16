namespace Wino.Core.Domain.Enums
{
    // From the SDK.
    public enum StorePurchaseResult
    {
        //
        // Summary:
        //     The purchase request succeeded.
        Succeeded,
        //
        // Summary:
        //     The current user has already purchased the specified app or add-on.
        AlreadyPurchased,
        //
        // Summary:
        //     The purchase request did not succeed.
        NotPurchased,
    }
}
