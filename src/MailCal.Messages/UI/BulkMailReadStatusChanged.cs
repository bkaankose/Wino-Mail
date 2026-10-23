using System;
using System.Collections.Generic;

namespace MailCal.Messaging.UI;

public record BulkMailReadStatusChanged(IReadOnlyList<Guid> UniqueIds);
