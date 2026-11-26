#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Finds the accounts other mail clients keep on this device. Only account details are read: never a password, a
/// token, a setting or mail. Each picked account is then added through the regular account setup.
/// </summary>
public interface IMailClientAccountImportService
{
    /// <summary>The clients this operating system can hold, in the order <see cref="ScanAsync"/> reads them.</summary>
    IReadOnlyList<MailClientImportSource> GetSources();

    /// <summary>
    /// Reads every source one by one and reports each before it is read. A client that is not installed, or whose files
    /// cannot be read, contributes nothing. An address found in several clients is listed once.
    /// </summary>
    Task<List<MailClientImportCandidate>> ScanAsync(IProgress<MailClientImportSource>? progress = null, CancellationToken cancellationToken = default);
}
