using System.Collections.Generic;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Service to access available fonts.
/// </summary>
public interface IFontService
{
    /// <summary>
    /// Get available fonts. Default + installed system fonts.
    /// Fonts initialized only once. To refresh fonts, restart the application.
    /// </summary>
    List<string> GetFonts();
}
