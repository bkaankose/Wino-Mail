using System.Collections.Generic;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Services
{
    public class FontService : IFontService
    {
        private readonly IPreferencesService _preferencesService;
        private ILogger _logger = Log.ForContext<FontService>();

        private readonly List<ReaderFontModel> _availableFonts =
        [
            new ReaderFontModel(ReaderFont.Arial, "Arial"),
            new ReaderFontModel(ReaderFont.Calibri, "Calibri"),
            new ReaderFontModel(ReaderFont.TimesNewRoman, "Times New Roman"),
            new ReaderFontModel(ReaderFont.TrebuchetMS, "Trebuchet MS"),
            new ReaderFontModel(ReaderFont.Tahoma, "Tahoma"),
            new ReaderFontModel(ReaderFont.Verdana, "Verdana"),
            new ReaderFontModel(ReaderFont.Georgia, "Georgia"),
            new ReaderFontModel(ReaderFont.CourierNew, "Courier New")
        ];

        public FontService(IPreferencesService preferencesService)
        {
            _preferencesService = preferencesService;
        }

        public List<ReaderFontModel> GetReaderFonts() => _availableFonts;

        public void ChangeReaderFont(ReaderFont font)
        {
            _preferencesService.ReaderFont = font;

            _logger.Information("Default reader font is changed to {Font}", font);
        }

        public void ChangeReaderFontSize(int size)
        {
            _preferencesService.ReaderFontSize = size;

            _logger.Information("Default reader font size is changed to {Size}", size);
        }

        public ReaderFontModel GetCurrentReaderFont() => _availableFonts.Find(f => f.Font == _preferencesService.ReaderFont);
        public int GetCurrentReaderFontSize() => _preferencesService.ReaderFontSize;
    }
}
