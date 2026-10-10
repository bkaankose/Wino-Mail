#!/bin/zsh
# Replaces the committed Sparkle framework with an official Sparkle release.
#
#   ./Sparkle/update-sparkle.sh 2.10.0
#
# Downloads Sparkle-<version>.tar.xz from github.com/sparkle-project/Sparkle, keeps only what Wino
# uses, and writes Sparkle/Native/Sparkle.xcframework and Sparkle/Native/Sparkle.version, which are
# committed. The release's sign_update and generate_keys tools are not committed;
# scripts/release/build-macos-release.sh downloads the same release for them.
#
# Removed from the framework:
# - Downloader.xpc: the app already has the network.client entitlement, so Sparkle downloads in process.
# - Headers, PrivateHeaders and Modules: the C# binding (SparkleApiDefinition.cs) does not need them.
# The framework stays ad-hoc signed; app signing re-signs it and its helpers (Wino.Mail.MacOS.csproj).
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <version>" >&2
  exit 64
fi

version=$1
root=${0:A:h}
native=$root/Native
url=https://github.com/sparkle-project/Sparkle/releases/download/$version/Sparkle-$version.tar.xz

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

echo "Downloading $url"
curl -fsSL -o $work/sparkle.tar.xz $url
mkdir $work/release
tar -xJf $work/sparkle.tar.xz -C $work/release

framework=$work/release/Sparkle.framework
rm -rf $framework/Versions/B/XPCServices/Downloader.xpc
rm -rf $framework/Versions/B/Headers $framework/Versions/B/PrivateHeaders $framework/Versions/B/Modules
rm -f $framework/Headers $framework/PrivateHeaders $framework/Modules

rm -rf $native/Sparkle.xcframework
mkdir -p $native
xcodebuild -create-xcframework -framework $framework -output $native/Sparkle.xcframework >/dev/null

{
  echo "version=$version"
  echo "url=$url"
  echo "sha256=$(shasum -a 256 $work/sparkle.tar.xz | cut -d' ' -f1)"
} > $native/Sparkle.version

echo "Wrote Sparkle $version to ${native#$root/}"
