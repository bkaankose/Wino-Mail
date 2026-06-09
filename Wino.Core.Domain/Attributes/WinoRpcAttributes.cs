using System;

namespace Wino.Core.Domain.Attributes;

/// <summary>
/// Marks a service interface as remoted between the UI process and the background
/// companion process. The RPC source generator emits a {Name}RemoteProxy (UI side),
/// a {Name}Dispatcher (companion side) and strongly typed request/response records
/// for every non-excluded member, and raises compile errors for members whose
/// signatures cannot cross the process boundary.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class WinoRpcServiceAttribute : Attribute;

/// <summary>
/// Excludes a member of a <see cref="WinoRpcServiceAttribute"/> interface from remoting.
/// Excluded members stay companion-internal (or UI-local); the generated proxy throws
/// <see cref="NotSupportedException"/> when they are called on the UI side.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class WinoRpcExcludeAttribute : Attribute;
