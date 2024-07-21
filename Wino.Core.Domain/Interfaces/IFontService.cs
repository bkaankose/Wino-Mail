using System.Collections.Generic;
using Wino.Domain.Enums;
using Wino.Domain.Models.Reader;

namespace Wino.Domain.Interfaces
{
    public interface IFontService
    {
        List<ReaderFontModel> GetReaderFonts();
        ReaderFontModel GetCurrentReaderFont();
        int GetCurrentReaderFontSize();

        void ChangeReaderFont(ReaderFont font);
        void ChangeReaderFontSize(int size);
    }
}
