#!/bin/zsh
# Copies the macOS Debug build into "Wino Lab.app" with bundle id com.winomail.macos.lab (its own sandbox container,
# apart from the installed Wino Mail profile), re-signed ad hoc with the build's entitlements.
set -e
SRC=${WINO_REPO:-$(cd "$(dirname "$0")/../../.." && pwd)}/src/Wino.Mail.MacOS/bin/Debug/net10.0-macos/osx-arm64/"Wino Mail.app"
LAB=${WINO_LAB_APP:-$HOME/Desktop/wino-lab-app/"Wino Lab.app"}
rm -rf "$LAB"; cp -R "$SRC" "$LAB"
/usr/libexec/PlistBuddy -c "Set :CFBundleIdentifier com.winomail.macos.lab" -c "Set :CFBundleName Wino Lab" "$LAB/Contents/Info.plist"
codesign -d --entitlements :- "$SRC" > /tmp/wino-lab-ent.plist 2>/dev/null
# Ad hoc signing cannot carry the team's restricted entitlements (push, Sign in with Apple, team and app identifiers).
for key in com.apple.application-identifier com.apple.developer.applesignin com.apple.developer.aps-environment com.apple.developer.team-identifier; do
  /usr/libexec/PlistBuddy -c "Delete :$key" /tmp/wino-lab-ent.plist 2>/dev/null || true
done
codesign --force --deep --sign - --entitlements /tmp/wino-lab-ent.plist "$LAB" 2>/dev/null
echo "$LAB"
