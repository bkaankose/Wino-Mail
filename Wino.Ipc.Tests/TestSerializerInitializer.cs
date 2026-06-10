using System.Runtime.CompilerServices;
using Wino.Ipc.Contracts;
using Wino.Ipc.Serialization;

namespace Wino.Ipc.Tests;

internal static class TestSerializerInitializer
{
    [ModuleInitializer]
    public static void Initialize() => WinoIpcJson.Initialize(WinoIpcJsonContext.Default);
}
