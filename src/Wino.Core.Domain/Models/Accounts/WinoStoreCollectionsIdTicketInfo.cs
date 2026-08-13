#nullable enable
using System;

namespace Wino.Core.Domain.Models.Accounts;

public sealed record WinoStoreCollectionsIdTicketInfo(
    string ServiceTicket,
    string PublisherUserId,
    DateTimeOffset ExpiresAtUtc);
