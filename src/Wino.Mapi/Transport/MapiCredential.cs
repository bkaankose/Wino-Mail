using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Mapi.Transport;

/// <summary>
/// How the transport authenticates. Exchange's MAPI virtual directory accepts OAuth bearer tokens
/// (the path Wino ships, via ExSTS) and Windows integrated auth; the wire is identical either way.
/// </summary>
public abstract record MapiCredential
{
    /// <summary>
    /// An OAuth bearer token. Held in memory only; never logged by the library. <paramref name="Refresh"/>,
    /// when given, mints a replacement: a session outlives the token's lifetime, and the transport
    /// asks for a fresh one (once) when the server answers 401, instead of failing the operation.
    /// </summary>
    public sealed record Bearer(string Token, Func<CancellationToken, Task<string?>>? Refresh = null) : MapiCredential;

    /// <summary>
    /// Windows integrated auth, with the scheme named explicitly. Exchange offers "Negotiate, NTLM";
    /// given the choice .NET picks Negotiate and does NOT fall back, so against a published name with
    /// no matching SPN (Kerberos cannot work) the attempt dies at 401 even though NTLM would have
    /// succeeded. Pin one. <paramref name="Credentials"/> null means the current Windows identity.
    /// </summary>
    public sealed record Integrated(string Scheme = "NTLM", NetworkCredential? Credentials = null) : MapiCredential;
}
