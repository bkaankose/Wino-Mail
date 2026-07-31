using System;
using System.Collections.Generic;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Messaging.Server;

/// <summary>
/// Raised when user performs search on the search bar.
/// </summary>
/// <param name="AccountIds">Accounts that performs the query. Multiple accounts for linked accounts.</param>
/// <param name="QueryText">Search query.</param>
/// <param name="Folders">Folders to include in search. All folders if null.</param>
public record OnlineSearchRequested(List<Guid> AccountIds, string QueryText, List<IMailItemFolder> Folders) : IClientMessage;
