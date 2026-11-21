#if DEBUG
using System.Globalization;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-next-folder</c> <c>s</c> path (answers the next folder picker, once), <c>debug-attachment</c> <c>s</c>
/// <c>open:n</c>, <c>save:n</c> or <c>save-all</c> on the current reader's strip.
/// </summary>
internal static partial class ReaderDebugActions
{
    static partial void RegisterAttachments(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-next-folder", "s", value =>
        {
            if (!Directory.Exists(value)) throw new DirectoryNotFoundException(value);
            FilePickers.DebugNext.Enqueue(value);
        });
        Add(application, "debug-attachment", "s", value =>
        {
            var view = Current();
            var strip = view.DebugAttachments ?? throw new InvalidOperationException("The reader has no attachment strip.");
            if (value == "save-all")
            {
                view.DebugSaveAll();
                return;
            }
            var parts = value.Split(':');
            var attachment = parts.Length == 2 ? strip.DebugAttachment(int.Parse(parts[1], CultureInfo.InvariantCulture)) : null;
            if (attachment is null) throw new ArgumentException($"No attachment for '{value}'.");
            if (parts[0] == "open") view.DebugOpenAttachment(attachment);
            else if (parts[0] == "save") view.DebugSaveAttachment(attachment);
            else throw new ArgumentException($"Unknown attachment command '{value}'.");
        });
    }
}
#endif
