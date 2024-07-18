using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Domain.Interfaces
{
    /// <summary>
    /// Service for managing fonts.
    /// </summary>
    public interface IFontService
    {
        /// <summary>
        /// Get available fonts. Default + installed system fonts.
        /// Fonts initialized only once. To refresh fonts, restart the application.
        /// </summary>
        List<string> GetFonts();

        /// <summary>
        /// Get current reader font.
        /// </summary>
        /// <returns>Returns font family. Never null.</returns>
        string GetCurrentReaderFont();
        int GetCurrentReaderFontSize();

        void SetReaderFont(string font);
        void SetReaderFontSize(int size);


        /// <summary>
        /// Get current composer font.
        /// </summary>
        /// <returns>Returns font family. Never null.</returns>
        string GetCurrentComposerFont();
        int GetCurrentComposerFontSize();

        void SetComposerFont(string font);
        void SetComposerFontSize(int size);
    }
}
