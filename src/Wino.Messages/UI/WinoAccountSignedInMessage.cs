using Wino.Core.Domain.Entities.Shared;

namespace Wino.Messaging.UI;

public record WinoAccountSignedInMessage(WinoAccount Account) : UIMessageBase<WinoAccountSignedInMessage>;
