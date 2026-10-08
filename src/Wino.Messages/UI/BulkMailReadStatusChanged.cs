using System;
using System.Collections.Generic;

namespace Wino.Messaging.UI;

public record BulkMailReadStatusChanged(IReadOnlyList<Guid> UniqueIds);
