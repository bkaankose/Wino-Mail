#!/bin/zsh
# labshot.sh NAME: captures every on-screen window of the running Wino Lab app (only that app) to /tmp/claude-501/NAME-<n>.png
P=$(pgrep -f "Wino Lab.app/Contents/MacOS" | head -1)
ids=$(osascript -l JavaScript -e 'ObjC.import("CoreGraphics"); var a=ObjC.castRefToObject($.CGWindowListCopyWindowInfo(1,0)); var out=[]; for (var i=0;i<a.count;i++){var w=a.objectAtIndex(i); if (w.objectForKey("kCGWindowOwnerPID").js=='"$P"') out.push(w.objectForKey("kCGWindowNumber").js);} out.join(" ")')
n=0; for id in ${=ids}; do n=$((n+1)); screencapture -x -o -l $id /tmp/claude-501/$1-$n.png; echo /tmp/claude-501/$1-$n.png; done
