#nullable enable
using System.Collections.Generic;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The languages a head's composer can check spelling in. A head whose editor reports them registers one; heads
/// without one offer the app languages.
/// </summary>
public interface ISpellCheckLanguageSource
{
    /// <summary>Language tags with the name to show, in display order. Empty when the platform reports none.</summary>
    IReadOnlyList<(string Code, string DisplayName)> GetLanguages();

    /// <summary>The listed language a saved code stands for, or null when the platform cannot check that language.</summary>
    string? Resolve(string? savedCode);
}
