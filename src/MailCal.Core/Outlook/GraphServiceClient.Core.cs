using System;
using Microsoft.Graph.Core.Requests;
using Microsoft.Kiota.Abstractions;

namespace Microsoft.Graph;

/// <summary>
/// Connects the path-filtered Kiota client to the paging, batching, and large-upload helpers
/// supplied by Microsoft.Graph.Core without taking a dependency on the monolithic Graph SDK.
/// </summary>
public partial class GraphServiceClient : IBaseClient, IDisposable
{
    public new IRequestAdapter RequestAdapter
    {
        get => base.RequestAdapter;
        set => base.RequestAdapter = value;
    }

    public BatchRequestBuilder Batch => new(RequestAdapter);

    public void Dispose()
    {
        if (RequestAdapter is IDisposable disposableAdapter)
        {
            disposableAdapter.Dispose();
        }
    }
}
