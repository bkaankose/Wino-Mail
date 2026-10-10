#!/bin/zsh
# Rebuilds the prebuilt native framework of one binding module.
#
#   ./build-native.sh StoreKit2
#
# Compiles <Module>/Swift/*.swift for arm64 and x86_64 and writes
# <Module>/Native/Wino<Module>.xcframework, which is committed. The .NET build only embeds that
# framework, so run this script only after changing the Swift source, then commit the result.
# Requires Xcode. The framework is left ad-hoc signed; app signing re-signs it.
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <Module>" >&2
  exit 64
fi

module=$1
root=${0:A:h}
source_dir=$root/$module/Swift
name=Wino$module
output=$root/$module/Native/$name.xcframework
# Keep in step with SupportedOSPlatformVersion in Wino.Mail.MacOS.csproj.
min_macos=14.0

sources=($source_dir/*.swift(N))
if (( ${#sources} == 0 )); then
  echo "No Swift sources in $source_dir" >&2
  exit 66
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

for arch in arm64 x86_64; do
  echo "Compiling $name for $arch"
  xcrun swiftc $sources \
    -module-name $name \
    -target $arch-apple-macos$min_macos \
    -sdk "$(xcrun --sdk macosx --show-sdk-path)" \
    -swift-version 6 \
    -parse-as-library \
    -emit-library \
    -O -whole-module-optimization \
    -Xlinker -install_name -Xlinker @rpath/$name.framework/Versions/A/$name \
    -o $work/$name-$arch
done

# A versioned (deep) bundle, which macOS code signing requires.
framework=$work/$name.framework
mkdir -p $framework/Versions/A/Resources
lipo -create $work/$name-arm64 $work/$name-x86_64 -output $framework/Versions/A/$name

cat > $framework/Versions/A/Resources/Info.plist <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>$name</string>
  <key>CFBundleIdentifier</key><string>com.winomail.bindings.$module:l</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>$name</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>$min_macos</string>
</dict>
</plist>
PLIST

ln -s A $framework/Versions/Current
ln -s Versions/Current/$name $framework/$name
ln -s Versions/Current/Resources $framework/Resources
codesign --force --sign - $framework

rm -rf $output
mkdir -p ${output:h}
xcodebuild -create-xcframework -framework $framework -output $output >/dev/null

# Records which source produced the committed binary.
shasum -a 256 $sources | sed "s|$root/||" > ${output:h}/$name.sources.sha256

echo "Wrote ${output#$root/}"
