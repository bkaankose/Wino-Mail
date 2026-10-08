using System;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Resolves application-owned relative resource paths for the current host.</summary>
public interface IApplicationResourceResolver
{
    Uri ResolvePackagedResource(string relativePath);
    Uri ResolveLocalResource(string relativePath);
}
