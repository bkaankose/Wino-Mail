using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Core.Domain.Messaging.UI;

public sealed record WinoIntelligenceEntitlementChanged(WinoIntelligenceEntitlementSnapshot Entitlement)
    : UIMessageBase<WinoIntelligenceEntitlementChanged>;
