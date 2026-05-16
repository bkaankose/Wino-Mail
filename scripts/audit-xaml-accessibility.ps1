param(
    [string]$Root = "Wino.Mail.WinUI"
)

$focusablePattern = '<\s*(Button|AppBarButton|AppBarToggleButton|ToggleButton|HyperlinkButton|SplitButton|ToggleSplitButton|ComboBox|TextBox|PasswordBox|CheckBox|RadioButton|ListView|GridView|TreeView|FlipView|PipsPager|MenuFlyoutItem)(?=[\s>/])[\s\S]*?>'
$namePattern = 'AutomationProperties\.Name|AutomationProperties\.LabeledBy|\b(Content|Label|Header|PlaceholderText|Text)\s*='

Get-ChildItem -Path $Root -Recurse -File -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object {
        $content = Get-Content -Path $_.FullName -Raw
        foreach ($match in [regex]::Matches($content, $focusablePattern)) {
            $tag = $match.Value

            if ($tag -notmatch $namePattern) {
                $lineNumber = ($content.Substring(0, $match.Index) -split "`r?`n").Count

                [PSCustomObject]@{
                    File = $_.FullName
                    Line = $lineNumber
                    Control = $match.Groups[1].Value
                    Text = ($tag -split "`r?`n")[0].Trim()
                }
            }
        }
    }
