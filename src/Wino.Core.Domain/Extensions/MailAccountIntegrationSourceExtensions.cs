using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Extensions;

public static class MailAccountIntegrationSourceExtensions
{
    /// <summary>
    /// Resolves the calendar source used at runtime.
    /// </summary>
    /// <remarks>
    /// Early integration-source builds stored special IMAP CalDAV accounts as
    /// <see cref="AccountIntegrationSource.Provider"/>. The explicit CalDAV support mode is
    /// sufficient evidence to interpret only those legacy records as DAV without changing the
    /// persisted account or silently falling back to local storage.
    /// </remarks>
    public static AccountIntegrationSource GetEffectiveCalendarIntegrationSource(this MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var isLegacyImapCalDavSource = account.ProviderType == MailProviderType.IMAP4 &&
            account.CalendarIntegrationSource == AccountIntegrationSource.Provider &&
            account.ServerInformation?.CalendarSupportMode == ImapCalendarSupportMode.CalDav;

        return isLegacyImapCalDavSource
            ? AccountIntegrationSource.Dav
            : account.CalendarIntegrationSource;
    }
}
