#!/bin/zsh
# Rebuilds the prebuilt native framework of one binding module.
#
#   ./build-native.sh StoreKit2
#
# Compiles <Module>/Swift/*.swift into three slices and writes
# <Module>/Native/Wino<Module>.xcframework, which is committed:
#   macos-arm64_x86_64     arm64 + x86_64, macOS 14.0, versioned (deep) framework
#   ios-arm64              arm64 device, iOS 17.0, shallow framework
#   ios-arm64-simulator    arm64 simulator, iOS 17.0, shallow framework
# The .NET build only embeds that framework, so run this script only after changing the Swift source
# or this script, then commit the result. Requires Xcode with the macOS and iOS SDKs.
# The frameworks are left ad-hoc signed; app signing re-signs them.
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
bundle_id=com.winomail.bindings.$module:l
# Keep in step with SupportedOSPlatformVersion in Wino.Core.Apple.Bindings.csproj and the app heads.
min_macos=14.0
min_ios=17.0

sources=($source_dir/*.swift(N))
if (( ${#sources} == 0 )); then
  echo "No Swift sources in $source_dir" >&2
  exit 66
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# compile <target triple> <sdk> <install name> <output>
# xcrun --sdk also sets SDKROOT, which the link step uses as its sysroot.
compile() {
  echo "Compiling $name for $1"
  xcrun --sdk $2 swiftc $sources \
    -module-name $name \
    -target $1 \
    -sdk "$(xcrun --sdk $2 --show-sdk-path)" \
    -swift-version 6 \
    -parse-as-library \
    -emit-library \
    -O -whole-module-optimization \
    -Xlinker -install_name -Xlinker $3 \
    -o $4
}

# macOS: a versioned (deep) bundle, which macOS code signing requires.
for arch in arm64 x86_64; do
  compile $arch-apple-macos$min_macos macosx @rpath/$name.framework/Versions/A/$name $work/$name-macos-$arch
done

macos_framework=$work/macos/$name.framework
mkdir -p $macos_framework/Versions/A/Resources
lipo -create $work/$name-macos-arm64 $work/$name-macos-x86_64 -output $macos_framework/Versions/A/$name

cat > $macos_framework/Versions/A/Resources/Info.plist <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>$name</string>
  <key>CFBundleIdentifier</key><string>$bundle_id</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>$name</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>$min_macos</string>
</dict>
</plist>
PLIST

ln -s A $macos_framework/Versions/Current
ln -s Versions/Current/$name $macos_framework/$name
ln -s Versions/Current/Resources $macos_framework/Resources
codesign --force --sign - $macos_framework

# iOS: shallow bundles (binary and Info.plist at the framework root), one per SDK.
# ios_framework <slice folder> <target triple> <sdk> <CFBundleSupportedPlatforms value>
ios_framework() {
  local framework=$work/$1/$name.framework
  mkdir -p $framework
  compile $2 $3 @rpath/$name.framework/$name $framework/$name
  cat > $framework/Info.plist <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>$name</string>
  <key>CFBundleIdentifier</key><string>$bundle_id</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>$name</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleSupportedPlatforms</key><array><string>$4</string></array>
  <key>CFBundleVersion</key><string>1</string>
  <key>MinimumOSVersion</key><string>$min_ios</string>
</dict>
</plist>
PLIST
  codesign --force --sign - $framework
}

ios_framework ios arm64-apple-ios$min_ios iphoneos iPhoneOS
ios_framework ios-simulator arm64-apple-ios$min_ios-simulator iphonesimulator iPhoneSimulator

rm -rf $output
mkdir -p ${output:h}
xcodebuild -create-xcframework \
  -framework $macos_framework \
  -framework $work/ios/$name.framework \
  -framework $work/ios-simulator/$name.framework \
  -output $output >/dev/null

# Records which inputs produced the committed binary: the module's Swift sources and this script,
# whose targets and framework layouts are part of the build.
shasum -a 256 $sources $root/build-native.sh | sed "s|$root/||" > ${output:h}/$name.sources.sha256

echo "Wrote ${output#$root/}"
