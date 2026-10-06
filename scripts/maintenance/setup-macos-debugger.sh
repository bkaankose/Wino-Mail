#!/bin/bash
# Install the C# extension's matching debugger with Microsoft's macOS 26 FIFO shim.
# This prepares tooling only; it does not build, sign, or launch the application.
set -euo pipefail

if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    echo "This debugger setup requires an Apple Silicon Mac." >&2
    exit 1
fi

code_cli="${WINO_VSCODE_CLI:-}"
if [[ -z "$code_cli" ]]; then
    code_cli="$(command -v code || true)"
fi
if [[ -z "$code_cli" ]]; then
    code_cli="/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code"
fi
extension_dir="$("$code_cli" --locate-extension ms-dotnettools.csharp)"
source_dir="$extension_dir/.debugger/arm64"
if [[ ! -x "$source_dir/vsdbg" || ! -f "$source_dir/version.txt" ]]; then
    echo "Install Microsoft's C# extension and let it finish installing its debugger." >&2
    exit 1
fi

shim_version="10.0.745401"
shim_package_sha="adcac4c69e2e5a29f6dc9df8a8ba589011a183bf505882cc37d137f63545375e"
shim_binary_sha="bcfab8ffb03c584f2704facd1ee6a5d9390e3692b12a0b5a55c5c15fb7132c49"
install_dir="$HOME/.local/share/wino-vsdbg"
expected_marker="$extension_dir|$(tr -d '\r\n' < "$source_dir/version.txt")|$shim_version|$shim_package_sha"
if [[ -x "$install_dir/vsdbg" && -f "$install_dir/.wino-setup" &&
      -f "$install_dir/libdbgshim.dylib" &&
      "$(cat "$install_dir/.wino-setup")" == "$expected_marker" ]] &&
   cmp -s "$source_dir/vsdbg" "$install_dir/vsdbg" &&
   [[ "$(shasum -a 256 "$install_dir/libdbgshim.dylib" | awk '{print $1}')" == "$shim_binary_sha" ]]; then
    echo "Mac debugger tooling is current."
    exit 0
fi

mkdir -p "$HOME/.local/share"
stage_dir="$(mktemp -d "$HOME/.local/share/wino-vsdbg-stage.XXXXXX")"
trap 'rm -rf "$stage_dir"' EXIT
package_file="$stage_dir/dbgshim.nupkg"
curl --fail --location --silent --show-error \
    "https://api.nuget.org/v3-flatcontainer/microsoft.diagnostics.dbgshim.osx-arm64/$shim_version/microsoft.diagnostics.dbgshim.osx-arm64.$shim_version.nupkg" \
    --output "$package_file"
actual_sha="$(shasum -a 256 "$package_file" | awk '{print $1}')"
if [[ "$actual_sha" != "$shim_package_sha" ]]; then
    echo "Microsoft diagnostic shim package checksum mismatch; installation stopped." >&2
    exit 1
fi

ditto "$source_dir" "$stage_dir/debugger"
unzip -p "$package_file" runtimes/osx-arm64/native/libdbgshim.dylib \
    > "$stage_dir/debugger/libdbgshim.dylib"
printf '%s\n' "$expected_marker" > "$stage_dir/debugger/.wino-setup"
if [[ -d "$install_dir" ]]; then
    previous_dir="$install_dir.previous.$(date +%Y%m%d%H%M%S)"
    mv "$install_dir" "$previous_dir"
    echo "Previous debugger preserved at $previous_dir"
fi
mv "$stage_dir/debugger" "$install_dir"
echo "Prepared $(cat "$install_dir/version.txt") with diagnostic shim $shim_version."
