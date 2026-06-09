using System;
using System.Collections.Generic;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Client.Accounts;

/// <summary>
/// Emitted when account menu items are reordered.
/// </summary>
/// <param name="newOrderDictionary">New order info.</param>
public record AccountMenuItemsReordered(Dictionary<Guid, int> newOrderDictionary) : IUIMessage;
