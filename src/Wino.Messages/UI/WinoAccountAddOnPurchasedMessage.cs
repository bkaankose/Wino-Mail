using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

public record WinoAccountAddOnPurchasedMessage(WinoAddOnProductType ProductType) : UIMessageBase<WinoAccountAddOnPurchasedMessage>;
