using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.ToDo;

/// <summary>Root view of the To Do page. Clear, so the theme backdrop shows around the Wino zone.</summary>
public sealed class ToDoPage : WinoSurfaceView
{
    public ToDoPage()
    {
        Fill = null;
        AccessibilityElement = false;
    }
}
