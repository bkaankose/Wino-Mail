#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// The platform half of the Wino Account browser sign-in flow: shows <paramref name="authorizationUri"/> in the system
/// browser surface and returns the <paramref name="callbackScheme"/> address the API redirected to. Throws
/// <see cref="OperationCanceledException"/> when the user closes the browser surface or the token is cancelled.
/// </summary>
public interface IWinoAccountBrowserAuthBroker
{
    Task<Uri> AuthorizeAsync(Uri authorizationUri, string callbackScheme, CancellationToken cancellationToken);
}
