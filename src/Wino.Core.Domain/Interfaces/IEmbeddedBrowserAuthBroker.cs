#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Shows a sign-in page inside the app and captures the redirect before the browser loads it. Used only by Exchange
/// modern-auth sign-in. Unlike <see cref="IOAuthBrowserBroker"/> it never opens the system browser, never listens on
/// loopback and never waits for a protocol activation; the redirect address can be any absolute URI the identity
/// provider has registered for the client. Hosts without an embedded browser do not register it.
/// </summary>
public interface IEmbeddedBrowserAuthBroker
{
    /// <summary>
    /// Opens <paramref name="startUri"/> and completes with the first navigation whose scheme, host, port and path
    /// match <paramref name="redirectUri"/>; that navigation is cancelled. Throws <see cref="OperationCanceledException"/>
    /// when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<EmbeddedBrowserAuthResult> AuthenticateAsync(Uri startUri, Uri redirectUri, CancellationToken cancellationToken);
}
