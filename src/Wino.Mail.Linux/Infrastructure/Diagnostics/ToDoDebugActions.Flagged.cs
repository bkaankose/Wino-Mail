#if DEBUG
using System.Globalization;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>Flagged mail verbs (MP-14): flagged, flagged-select, flagged-complete i, flagged-open i.</summary>
internal static partial class ToDoDebugActions
{
    static partial void AddFlaggedVerbs()
    {
        Verb("flagged", _ => Page.DebugDescribeFlaggedMail());
        Verb("flagged-select", async _ =>
        {
            var page = Page;
            await page.ViewModel.SelectPresentationSurfaceAsync(TaskViewKind.FlaggedMail, null);
            await Task.Delay(500);
            return page.DebugDescribeFlaggedMail();
        });
        Verb("flagged-complete", args =>
        {
            var page = Page;
            var item = page.DebugFlaggedItem(int.Parse(args.Length > 0 ? args[0] : "0", CultureInfo.InvariantCulture)) ?? throw new ArgumentException("no row");
            _ = page.ViewModel.FlaggedMail.CompleteCommand.ExecuteAsync(item);
            return "completing " + item.Subject;
        });
        Verb("flagged-open", args =>
        {
            var page = Page;
            var item = page.DebugFlaggedItem(int.Parse(args.Length > 0 ? args[0] : "0", CultureInfo.InvariantCulture)) ?? throw new ArgumentException("no row");
            _ = page.ViewModel.FlaggedMail.OpenCommand.ExecuteAsync(item);
            return "opening " + item.Subject;
        });
    }
}
#endif
