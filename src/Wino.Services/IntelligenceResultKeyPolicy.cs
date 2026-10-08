#nullable enable
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Services;

public enum IntelligenceResultKeyAction
{
    None,

    /// <summary>Make sure the Wino user has a key, creating one when missing.</summary>
    Ensure,

    /// <summary>Delete every key, abandon jobs, keep imported results.</summary>
    Remove,
}

/// <summary>
/// The whole business rule for the device result key, as one pure function of the entitlement
/// snapshot. Only Active creates a key. Only an authoritative end of the add-on, or a sign-out,
/// removes one. Quota exhaustion and an API that is not answering properly never remove
/// anything, however long they last.
/// </summary>
public static class IntelligenceResultKeyPolicy
{
    public static bool ShouldHoldKey(WinoIntelligenceEntitlementSnapshot snapshot)
        => snapshot.State is WinoIntelligenceEntitlementState.Active
            or WinoIntelligenceEntitlementState.QuotaExhausted
            or WinoIntelligenceEntitlementState.Unavailable;

    public static bool ShouldRemoveKey(WinoIntelligenceEntitlementSnapshot snapshot)
        => snapshot.State switch
        {
            WinoIntelligenceEntitlementState.SignedOut => true,
            WinoIntelligenceEntitlementState.Expired or WinoIntelligenceEntitlementState.NoSubscription => snapshot.IsAuthoritative,
            _ => false,
        };

    /// <param name="keyMayExist">
    /// Whether this device may hold a key. False means the key store is never touched, which is
    /// what keeps users without the add-on completely out of the intelligence code paths.
    /// </param>
    public static IntelligenceResultKeyAction Decide(WinoIntelligenceEntitlementSnapshot snapshot, bool keyMayExist)
    {
        if (snapshot.State == WinoIntelligenceEntitlementState.Active && snapshot.WinoAccountId is not null)
        {
            return IntelligenceResultKeyAction.Ensure;
        }

        return keyMayExist && ShouldRemoveKey(snapshot)
            ? IntelligenceResultKeyAction.Remove
            : IntelligenceResultKeyAction.None;
    }
}
