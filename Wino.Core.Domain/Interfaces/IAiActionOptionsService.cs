using System.Collections.Generic;
using Wino.Core.Domain.Models.Ai;

namespace Wino.Core.Domain.Interfaces;

public interface IAiActionOptionsService
{
    IReadOnlyList<AiTranslateLanguageOption> GetTranslateLanguageOptions();
    IReadOnlyList<AiRewriteModeOption> GetRewriteModeOptions();
}
