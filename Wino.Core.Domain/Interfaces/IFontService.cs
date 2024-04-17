using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Domain.Interfaces
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
