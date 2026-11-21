#if DEBUG
using Wino.Core.Domain.Enums;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-reader-invitation</c> <c>s</c> <c>expand</c>, <c>collapse</c>, <c>accept</c>, <c>tentative</c>, <c>decline</c>,
/// <c>open</c>, <c>remove</c> or <c>comment:&lt;text&gt;</c> on the current reader's card, as its widgets would; logs the card.
/// </summary>
internal static partial class ReaderDebugActions
{
    static partial void RegisterInvitation(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-reader-invitation", "s", value =>
        {
            var card = Current().DebugInvitation ?? throw new InvalidOperationException("The reader has no invitation card.");
            switch (value)
            {
                case "expand": card.IsExpanded = true; break;
                case "collapse": card.IsExpanded = false; break;
                case "accept": card.DebugRespond(AttendeeStatus.Accepted); break;
                case "tentative": card.DebugRespond(AttendeeStatus.Tentative); break;
                case "decline": card.DebugRespond(AttendeeStatus.Declined); break;
                case "open": card.DebugOpen(); break;
                case "remove": card.DebugRemove(); break;
                default:
                    if (!value.StartsWith("comment:", StringComparison.Ordinal)) throw new ArgumentException($"Unknown invitation command '{value}'.");
                    card.DebugComment(value["comment:".Length..]);
                    break;
            }
            Serilog.Log.Information("Debug action: {Card}", card.Describe());
        });
    }
}
#endif
