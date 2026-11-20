using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Messaging.UI;

public record BulkMailReadStatusChanged(IReadOnlyList<Guid> UniqueIds);
