using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Reader
{
    public class EditorToolbarSection
    {
        public EditorToolbarSectionType SectionType { get; set; }
        public string Title
        {
            get
            {
                switch (SectionType)
                {
                    case EditorToolbarSectionType.None:
                        return Translator.EditorToolbarOption_None;
                    case EditorToolbarSectionType.Format:
                        return Translator.EditorToolbarOption_Format;
                    case EditorToolbarSectionType.Insert:
                        return Translator.EditorToolbarOption_Insert;
                    case EditorToolbarSectionType.Draw:
                        return Translator.EditorToolbarOption_Draw;
                    case EditorToolbarSectionType.Options:
                        return Translator.EditorToolbarOption_Options;
                    default:
                        return "Unknown Editor Option";
                }
            }
        }
    }
}
