# Bundled Visual C++ runtime

`onnxruntime.dll` from `Microsoft.ML.OnnxRuntime` dynamically links the Visual C++ runtime (`msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll`, and on x64 `vcruntime140_1.dll`). The Wino MSIX declares no `Microsoft.VCLibs` dependency, so without these files the app depends on whatever copy the user's machine has in System32. Older copies do not merely fail to load: since the 14.40 toolset the STL's `std::mutex` is laid out differently, and `onnxruntime.dll` crashes the process natively the first time it locks one. Users saw this as a crash when opening mails with attachments.

`Wino.Core.ML.csproj` copies the folder that matches the build architecture into every consumer's output and package root, next to `onnxruntime.dll`. The Windows loader searches the application directory before System32, so the bundled copy wins. `OnnxNativeRuntimeSupport` still checks the copy that will actually be used (an already loaded module, then bundled, then system) and marks the model unavailable instead of loading ONNX when it is missing or older than 14.40.

| Folder | Source |
| --- | --- |
| `win-x64` | Visual Studio 2026 `VC\Redist\MSVC\14.51.36231\x64\Microsoft.VC145.CRT`, file version 14.51.36247.0 |
| `win-arm64` | Visual Studio 2026 `VC\Redist\MSVC\14.51.36231\arm64\Microsoft.VC145.CRT`, file version 14.51.36247.0 |

Microsoft's Visual Studio redistribution terms permit shipping these files with an application. x86 has no folder because `Wino.Core.ML` compiles without ONNX there.

## Updating

1. Open `VC\Redist\MSVC\<version>\<arch>\Microsoft.VC14x.CRT` under a Visual Studio installation whose toolset is at least as new as the one `onnxruntime.dll` was built with (take the newest installed redistributable when in doubt).
2. Copy the four DLLs for x64 and arm64 over the existing files.
3. Update the table above, then rebuild and run `tests/Wino.Core.ML.Tests`, which checks that the copied files land next to `onnxruntime.dll`.
