using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Messaging.UI;

/// <summary>Links were stored or removed. Screens that show linked items reload when a changed link mentions their item.</summary>
public record ItemLinksChanged(IReadOnlyList<ItemLink> Links) : UIMessageBase<ItemLinksChanged>;
