using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Requests;

/// <summary>
/// Encapsulates request to queue and account for synchronizer.
/// </summary>
/// <param name="AccountId">Which account to execute this request for.</param>
/// <param name="Request">Prepared request for the server.</param>
public record ServerRequestPackage(Guid AccountId, IRequestBase Request) : IClientMessage
{
    public override string ToString() => $"Server Package: {Request.GetType().Name}";
}
