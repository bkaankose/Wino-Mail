using System;
using System.Collections.Generic;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.UI;

public record BulkMailReadStatusChanged(IReadOnlyList<Guid> UniqueIds) : IUIMessage;
