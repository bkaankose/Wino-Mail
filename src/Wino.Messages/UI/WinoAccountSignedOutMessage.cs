using Wino.Core.Domain.Entities.Shared;

namespace Wino.Messaging.UI;

public record WinoAccountSignedOutMessage(WinoAccount Account) : UIMessageBase<WinoAccountSignedOutMessage>;
