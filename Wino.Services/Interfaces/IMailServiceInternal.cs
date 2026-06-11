using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Services;

/// <summary>
/// Companion-process-only surface of <see cref="IMailService"/>. NewMailItemPackage carries a
/// MimeMessage, so these synchronizer-internal members cannot live in Wino.Core.Domain.
/// </summary>
public interface IMailServiceInternal : IMailService
{
    Task<bool> CreateMailAsync(Guid accountId, NewMailItemPackage package);

    Task CreateMailsAsync(Guid accountId, IReadOnlyList<NewMailItemPackage> packages);

    /// <summary>
    /// Creates a new mail from a package without doing any existence check.
    /// Use it with caution.
    /// </summary>
    Task CreateMailRawAsync(MailAccount account, MailItemFolder mailItemFolder, NewMailItemPackage package);
}
