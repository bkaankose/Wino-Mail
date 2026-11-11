#!/bin/zsh
# labclick.sh TITLE: clicks the first button titled TITLE in the Wino Lab app's front window (alerts included).
P=$(pgrep -f "Wino Lab.app/Contents/MacOS" | head -1)
osascript -e "tell application \"System Events\" to tell (first process whose unix id is $P)
repeat with w in windows
  repeat with e in (entire contents of w)
  try
    if (role of e is \"AXButton\") and (title of e is \"$1\" or name of e is \"$1\") then
      click e
      return true
    end if
  end try
  end repeat
end repeat
return false
end tell"
