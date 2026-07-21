using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Launch;

public sealed record MailToastComposeRequest(Guid MailItemUniqueId, MailOperation Action);
