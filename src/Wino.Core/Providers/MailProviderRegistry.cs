#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Providers;

public sealed class MailProviderRegistry : IMailProviderRegistry
{
    // The modules are resolved on first use, not in the constructor: they depend on storage services
    // (task, mail, account) that read provider traits from this registry, which would be a cycle.
    private readonly Lazy<IReadOnlyList<IMailProviderModule>> _modules;
    private readonly Lazy<Dictionary<MailProviderType, IMailProviderModule>> _byType;

    public MailProviderRegistry(Func<IEnumerable<IMailProviderModule>> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        _modules = new Lazy<IReadOnlyList<IMailProviderModule>>(() => modules().ToList());
        _byType = new Lazy<Dictionary<MailProviderType, IMailProviderModule>>(() => BuildLookup(_modules.Value));
    }

    public IReadOnlyList<IMailProviderModule> Modules => _modules.Value;

    public IMailProviderModule? Find(MailProviderType providerType)
        => _byType.Value.TryGetValue(providerType, out var module) ? module : null;

    public IMailProviderModule Get(MailProviderType providerType)
        => Find(providerType) ?? throw new NotSupportedException(Translator.Exception_UnsupportedProvider);

    public MailProviderTraits GetTraits(MailProviderType providerType)
        => Find(providerType)?.Traits ?? MailProviderTraits.None;

    private static Dictionary<MailProviderType, IMailProviderModule> BuildLookup(IReadOnlyList<IMailProviderModule> modules)
    {
        var lookup = new Dictionary<MailProviderType, IMailProviderModule>();
        foreach (var module in modules)
        {
            if (!lookup.TryAdd(module.ProviderType, module))
                throw new InvalidOperationException($"More than one provider module is registered for {module.ProviderType}.");
        }

        return lookup;
    }
}
