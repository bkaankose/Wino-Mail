using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Wino.Core.MacOS.Bindings.StoreKit2;

/// <summary>
/// P/Invoke surface of WinoStoreKit2.framework (Swift/WinoStoreKit2.swift). Each export answers once
/// through <see cref="OnReply"/> with a JSON envelope; the context is a GCHandle to the waiting call.
/// </summary>
internal static partial class StoreKit2Native
{
    private const string Library = "@rpath/WinoStoreKit2.framework/WinoStoreKit2";

    [LibraryImport(Library, EntryPoint = "wino_sk2_storefront")]
    internal static partial void Storefront(nint callback, nint context);

    [LibraryImport(Library, EntryPoint = "wino_sk2_products", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void Products(string idsJson, nint callback, nint context);

    [LibraryImport(Library, EntryPoint = "wino_sk2_purchase", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void Purchase(string productId, string? appAccountToken, nint callback, nint context);

    [LibraryImport(Library, EntryPoint = "wino_sk2_current_entitlements")]
    internal static partial void CurrentEntitlements(nint callback, nint context);

    [LibraryImport(Library, EntryPoint = "wino_sk2_unfinished")]
    internal static partial void Unfinished(nint callback, nint context);

    [LibraryImport(Library, EntryPoint = "wino_sk2_finish")]
    internal static partial void Finish(ulong transactionId, nint callback, nint context);

    [LibraryImport(Library, EntryPoint = "wino_sk2_sync")]
    internal static partial void Sync(nint callback, nint context);

    [LibraryImport(Library, EntryPoint = "wino_sk2_start_updates")]
    internal static partial void StartUpdates(nint callback, nint context);

    [LibraryImport(Library, EntryPoint = "wino_sk2_stop_updates")]
    internal static partial void StopUpdates();

    [LibraryImport(Library, EntryPoint = "wino_sk2_request_review")]
    internal static partial void RequestReview();

    /// <summary>Native pointer to <see cref="OnReply"/>, passed as the callback of every one-shot export.</summary>
    internal static unsafe nint ReplyCallback => (nint)(delegate* unmanaged<nint, nint, void>)&OnReply;

    /// <summary>Starts a native call and awaits its single reply.</summary>
    internal static async Task<T?> CallAsync<T>(Action<nint> start, JsonTypeInfo<StoreKit2Envelope<T>> typeInfo)
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(reply);
        try
        {
            start(GCHandle.ToIntPtr(handle));
        }
        catch
        {
            handle.Free();
            throw;
        }

        return Unwrap(await reply.Task.ConfigureAwait(false), typeInfo);
    }

    internal static T? Unwrap<T>(string json, JsonTypeInfo<StoreKit2Envelope<T>> typeInfo)
    {
        var envelope = JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new StoreKit2Exception("StoreKit returned an empty reply.");
        return envelope.Ok ? envelope.Value : throw new StoreKit2Exception(envelope.Error ?? "StoreKit call failed.");
    }

    [UnmanagedCallersOnly]
    private static void OnReply(nint context, nint json)
    {
        var handle = GCHandle.FromIntPtr(context);
        var reply = handle.Target as TaskCompletionSource<string>;
        handle.Free();
        reply?.TrySetResult(Marshal.PtrToStringUTF8(json) ?? string.Empty);
    }
}
