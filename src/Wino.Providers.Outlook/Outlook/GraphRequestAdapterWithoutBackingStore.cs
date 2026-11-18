using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Abstractions.Store;

namespace Wino.Core.Outlook;

/// <summary>
/// The official GraphServiceClient always calls EnableBackingStore on its adapter, which turns on change tracking:
/// a property set to null is written as an explicit JSON null, and a model serialized once sends only properties
/// changed afterwards. The Outlook code relies on plain serialization (null means omit), as the generated client did,
/// so this adapter ignores that call and leaves the serialization and parse factories untouched.
/// </summary>
internal sealed class GraphRequestAdapterWithoutBackingStore(IRequestAdapter inner) : IRequestAdapter
{
    public void EnableBackingStore(IBackingStoreFactory backingStoreFactory) { }

    public ISerializationWriterFactory SerializationWriterFactory => inner.SerializationWriterFactory;

    public string BaseUrl
    {
        get => inner.BaseUrl;
        set => inner.BaseUrl = value;
    }

    public Task<ModelType> SendAsync<ModelType>(RequestInformation requestInfo, ParsableFactory<ModelType> factory, Dictionary<string, ParsableFactory<IParsable>> errorMapping = null, CancellationToken cancellationToken = default) where ModelType : IParsable
        => inner.SendAsync(requestInfo, factory, errorMapping, cancellationToken);

    public Task<IEnumerable<ModelType>> SendCollectionAsync<ModelType>(RequestInformation requestInfo, ParsableFactory<ModelType> factory, Dictionary<string, ParsableFactory<IParsable>> errorMapping = null, CancellationToken cancellationToken = default) where ModelType : IParsable
        => inner.SendCollectionAsync(requestInfo, factory, errorMapping, cancellationToken);

    public Task<ModelType> SendPrimitiveAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] ModelType>(RequestInformation requestInfo, Dictionary<string, ParsableFactory<IParsable>> errorMapping = null, CancellationToken cancellationToken = default)
        => inner.SendPrimitiveAsync<ModelType>(requestInfo, errorMapping, cancellationToken);

    public Task<IEnumerable<ModelType>> SendPrimitiveCollectionAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] ModelType>(RequestInformation requestInfo, Dictionary<string, ParsableFactory<IParsable>> errorMapping = null, CancellationToken cancellationToken = default)
        => inner.SendPrimitiveCollectionAsync<ModelType>(requestInfo, errorMapping, cancellationToken);

    public Task SendNoContentAsync(RequestInformation requestInfo, Dictionary<string, ParsableFactory<IParsable>> errorMapping = null, CancellationToken cancellationToken = default)
        => inner.SendNoContentAsync(requestInfo, errorMapping, cancellationToken);

    public Task<T> ConvertToNativeRequestAsync<T>(RequestInformation requestInfo, CancellationToken cancellationToken = default)
        => inner.ConvertToNativeRequestAsync<T>(requestInfo, cancellationToken);
}
