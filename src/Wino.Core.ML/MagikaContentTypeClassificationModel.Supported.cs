#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.ML;

public sealed class MagikaContentTypeClassificationModel : IContentTypeClassificationModel, IDisposable
{
    private readonly string _modelDirectory;
    private readonly Lazy<Task<ModelState>> _modelState;
    private bool _disposed;

    public bool IsSupported => true;

    public MagikaContentTypeClassificationModel()
        : this(MagikaAssetLoader.GetDefaultModelDirectory())
    {
    }

    internal MagikaContentTypeClassificationModel(string modelDirectory)
    {
        _modelDirectory = modelDirectory;
        _modelState = new Lazy<Task<ModelState>>(
            () => Task.Run(LoadModelState),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async ValueTask<ContentTypeClassificationResult> ClassifyAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!content.CanRead || !content.CanSeek)
            throw new ArgumentException("Content must be a readable, seekable stream.", nameof(content));

        try
        {
            var state = await _modelState.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            var originalPosition = content.Position;

            try
            {
                if (content.Length == 0)
                    return CreateDirectResult(state, "empty", 1);

                if (content.Length < state.Configuration.MinimumFileSize)
                    return CreateFewBytesResult(state, ReadBytes(content, 0, checked((int)content.Length)));

                var features = ExtractFeatures(content, state.Configuration);
                if (features[state.Configuration.MinimumFileSize - 1] == state.Configuration.PaddingToken)
                {
                    var length = checked((int)Math.Min(content.Length, state.Configuration.BlockSize));
                    return CreateFewBytesResult(state, ReadBytes(content, 0, length));
                }

                var predictions = await state.Session.RunAsync(features, cancellationToken).ConfigureAwait(false);
                if (predictions.Length != state.Configuration.Labels.Length)
                    throw new InvalidDataException("Magika model output does not match its configured label space.");

                var index = 0;
                for (var i = 1; i < predictions.Length; i++)
                {
                    if (predictions[i] > predictions[index])
                        index = i;
                }

                var rawLabel = state.Configuration.Labels[index];
                var score = predictions[index];
                var threshold = state.Configuration.Thresholds.TryGetValue(rawLabel, out var configuredThreshold)
                    ? configuredThreshold
                    : state.Configuration.DefaultThreshold;
                var outputLabel = state.Configuration.OverwriteMap.TryGetValue(rawLabel, out var overwrite)
                    ? overwrite
                    : rawLabel;
                var status = score >= threshold
                    ? ContentTypeClassificationStatus.Detected
                    : ContentTypeClassificationStatus.LowConfidence;

                if (status == ContentTypeClassificationStatus.LowConfidence)
                {
                    outputLabel = state.KnowledgeBase.TryGetValue(outputLabel, out var rawInformation) && rawInformation.IsText
                        ? "txt"
                        : "unknown";
                }

                return CreateResult(state, status, rawLabel, outputLabel, score);
            }
            finally
            {
                content.Position = originalPosition;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new ContentTypeClassificationResult(ContentTypeClassificationStatus.Failed);
        }
    }

    private ModelState LoadModelState()
    {
        var configuration = MagikaAssetLoader.LoadConfiguration(_modelDirectory);
        var knowledgeBase = MagikaAssetLoader.LoadKnowledgeBase(_modelDirectory);
        var session = new OnnxModelSessionManager(MagikaAssetLoader.ValidateAndGetModelPath(_modelDirectory));

        if (session.OutputLength != configuration.Labels.Length)
        {
            session.Dispose();
            throw new InvalidDataException("Magika model output does not match its configured label space.");
        }

        return new ModelState(configuration, knowledgeBase, session);
    }

    private static int[] ExtractFeatures(Stream content, MagikaModelConfiguration configuration)
    {
        var readLength = checked((int)Math.Min(content.Length, configuration.BlockSize));
        var beginning = TrimStart(ReadBytes(content, 0, readLength));
        var end = TrimEnd(ReadBytes(content, content.Length - readLength, readLength));
        var features = Enumerable.Repeat(configuration.PaddingToken, configuration.BeginningSize + configuration.EndSize).ToArray();

        var beginningCount = Math.Min(configuration.BeginningSize, beginning.Length);
        for (var i = 0; i < beginningCount; i++)
            features[i] = beginning[i];

        var endCount = Math.Min(configuration.EndSize, end.Length);
        var endSourceOffset = end.Length - endCount;
        var endDestinationOffset = configuration.BeginningSize + configuration.EndSize - endCount;
        for (var i = 0; i < endCount; i++)
            features[endDestinationOffset + i] = end[endSourceOffset + i];

        return features;
    }

    private static byte[] ReadBytes(Stream content, long offset, int count)
    {
        content.Position = offset;
        var buffer = new byte[count];
        var totalRead = 0;

        while (totalRead < count)
        {
            var read = content.Read(buffer, totalRead, count - totalRead);
            if (read == 0)
                break;

            totalRead += read;
        }

        return totalRead == buffer.Length ? buffer : buffer[..totalRead];
    }

    private static byte[] TrimStart(byte[] value)
    {
        var index = 0;
        while (index < value.Length && IsWhitespace(value[index]))
            index++;

        return value[index..];
    }

    private static byte[] TrimEnd(byte[] value)
    {
        var index = value.Length;
        while (index > 0 && IsWhitespace(value[index - 1]))
            index--;

        return value[..index];
    }

    private static bool IsWhitespace(byte value) => value is 9 or 10 or 11 or 12 or 13 or 32;

    private static ContentTypeClassificationResult CreateFewBytesResult(ModelState state, byte[] content)
    {
        var label = IsUtf8(content) ? "txt" : "unknown";
        return CreateDirectResult(state, label, 1);
    }

    private static bool IsUtf8(byte[] content)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static ContentTypeClassificationResult CreateDirectResult(ModelState state, string label, double score) =>
        CreateResult(state, ContentTypeClassificationStatus.Detected, "undefined", label, score);

    private static ContentTypeClassificationResult CreateResult(
        ModelState state,
        ContentTypeClassificationStatus status,
        string rawLabel,
        string outputLabel,
        double score)
    {
        state.KnowledgeBase.TryGetValue(outputLabel, out var information);

        return new ContentTypeClassificationResult(
            status,
            outputLabel,
            rawLabel,
            information?.MimeType,
            information?.Description,
            information?.Group,
            score,
            information?.Extensions ?? Array.Empty<string>());
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!_modelState.IsValueCreated)
            return;

        var modelState = _modelState.Value;
        if (modelState.IsCompletedSuccessfully)
        {
            modelState.Result.Session.Dispose();
            return;
        }

        _ = modelState.ContinueWith(
            completed =>
            {
                if (completed.IsCompletedSuccessfully)
                    completed.Result.Session.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record ModelState(
        MagikaModelConfiguration Configuration,
        IReadOnlyDictionary<string, MagikaContentTypeInformation> KnowledgeBase,
        OnnxModelSessionManager Session);
}
