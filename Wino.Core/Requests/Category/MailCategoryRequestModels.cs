using System.Collections.Generic;

namespace Wino.Core.Requests.Category;

public sealed record MailCategoryMessageUpdateTarget(string MessageId, IReadOnlyList<string> CategoryNames);
