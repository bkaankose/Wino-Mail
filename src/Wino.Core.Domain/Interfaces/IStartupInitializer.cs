namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Shared configuration that runs once at the start of runtime initialization on every head, before the database and
/// the other startup steps. Implementations may only use registrations and must not wait on databases, the network or
/// other startup work.
/// </summary>
public interface IStartupInitializer : IInitializeAsync
{
}
