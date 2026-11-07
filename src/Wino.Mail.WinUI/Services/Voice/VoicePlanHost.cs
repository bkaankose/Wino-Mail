#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.WinUI.Services.Voice;

/// <summary>The Windows side of <see cref="IVoicePlanHost"/>: hands each step to the app, which opens the shell window.</summary>
internal sealed class VoicePlanHost(App app) : IVoicePlanHost
{
    public Task OpenSearchAsync(MailAccount account, IReadOnlyList<IMailItemFolder> folders, MailSearchCriteria criteria)
        => app.OpenVoiceSearchAsync(account, folders, criteria);

    public Task OpenMailAsync(MailCopy mail) => app.OpenVoiceMailAsync(mail);

    public Task OpenDraftAsync(MailAccount account, DraftCreationOptions options, MailCopy? referenceMail)
        => app.OpenVoiceDraftAsync(account, options, referenceMail);

    public Task OpenEventAsync(CalendarItem calendarItem) => app.OpenVoiceEventAsync(calendarItem);

    public Task OpenEventEditorAsync(CalendarEventComposeNavigationArgs args) => app.OpenVoiceEventEditorAsync(args);
}
