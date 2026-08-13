using System;

namespace Wino.Core.Domain.Enums;

[Flags]
public enum AiActionType
{
    None = 0,
    Translate = 1,
    Rewrite = 2,
    Summarize = 4,
}
