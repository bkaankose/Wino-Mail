#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Wino.Core.ML;

internal sealed class OnnxModelSessionManager : IDisposable
{
    private readonly InferenceSession _session;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly string _inputName;

    public int OutputLength { get; }

    public OnnxModelSessionManager(string modelPath)
    {
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.Single();
        OutputLength = checked((int)_session.OutputMetadata.Values.Single().Dimensions.Last());
    }

    public async ValueTask<float[]> RunAsync(int[] features, CancellationToken cancellationToken)
    {
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var tensor = new DenseTensor<int>(features, new[] { 1, features.Length });
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            return results.Single().AsEnumerable<float>().ToArray();
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _inferenceLock.Dispose();
    }
}
