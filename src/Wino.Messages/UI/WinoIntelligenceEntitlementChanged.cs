using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Messaging.UI;

public sealed record WinoIntelligenceEntitlementChanged(WinoIntelligenceEntitlementSnapshot Entitlement)
    : UIMessageBase<WinoIntelligenceEntitlementChanged>;
