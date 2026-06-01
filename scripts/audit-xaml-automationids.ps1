param(
    [string]$Root = "Wino.Mail.WinUI",
    [switch]$Fix
)

$ErrorActionPreference = "Stop"

$controlTypes = @(
    "AppBarButton",
    "AppBarToggleButton",
    "AutoSuggestBox",
    "Button",
    "CalendarView",
    "CheckBox",
    "ComboBox",
    "DatePicker",
    "FlipView",
    "GridView",
    "HyperlinkButton",
    "ListBox",
    "ListView",
    "MenuFlyoutItem",
    "NavigationView",
    "NavigationViewItem",
    "NumberBox",
    "PasswordBox",
    "PipsPager",
    "RadioButton",
    "RichEditBox",
    "Slider",
    "SplitButton",
    "TabView",
    "TabViewItem",
    "TextBox",
    "TimePicker",
    "ToggleButton",
    "ToggleSplitButton",
    "ToggleSwitch",
    "TreeView",
    "SettingsCard",
    "SettingsExpander",
    "WinoNavigationViewItem"
)

$controlPattern = $controlTypes -join "|"
$tagPattern = "(?s)<(?<prefix>[A-Za-z_][\w]*:)?(?<type>$controlPattern)(?<attrs>(?:\s|/|>).*?)(?<close>/?>)"
$namePattern = '(?<![\w.:])x:Name\s*=\s*"(?<name>[^"]+)"|(?<![\w.:])Name\s*=\s*"(?<name>[^"]+)"'
$automationIdPattern = '\bAutomationProperties\.AutomationId\s*='

function Convert-ToAutomationIdPart {
    param([string]$Value)

    $clean = [regex]::Replace($Value, "[^A-Za-z0-9]+", " ")
    $parts = @($clean -split "\s+" | Where-Object { $_ })
    if ($parts.Count -eq 0) {
        return "Control"
    }

    return ($parts | ForEach-Object {
        if ($_.Length -eq 1) {
            $_.ToUpperInvariant()
        }
        else {
            $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1)
        }
    }) -join ""
}

function Get-ControlIdBase {
    param(
        [System.Text.RegularExpressions.Match]$Match,
        [string]$FileStem,
        [string]$ControlType
    )

    $nameMatch = [regex]::Match($Match.Value, $namePattern)
    if ($nameMatch.Success) {
        return Convert-ToAutomationIdPart $nameMatch.Groups["name"].Value
    }

    return "$(Convert-ToAutomationIdPart $FileStem)$ControlType"
}

$missing = New-Object System.Collections.Generic.List[object]
$rootPath = (Resolve-Path $Root).Path

Get-ChildItem -Path $rootPath -Recurse -File -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object {
        $file = $_
        $content = Get-Content -Path $file.FullName -Raw
        $matches = [regex]::Matches($content, $tagPattern)
        $replacements = New-Object System.Collections.Generic.List[object]
        $usedIds = New-Object System.Collections.Generic.HashSet[string]

        foreach ($match in $matches) {
            $tagText = $match.Value

            if ($tagText -match $automationIdPattern) {
                $existingId = [regex]::Match($tagText, 'AutomationProperties\.AutomationId\s*=\s*"(?<id>[^"]+)"')
                if ($existingId.Success) {
                    [void]$usedIds.Add($existingId.Groups["id"].Value)
                }
                continue
            }

            $lineNumber = ($content.Substring(0, $match.Index) -split "`r?`n").Count
            $controlType = $match.Groups["type"].Value
            $baseId = Get-ControlIdBase -Match $match -FileStem $file.BaseName -ControlType $controlType
            $candidate = $baseId
            $suffix = 2

            while (-not $usedIds.Add($candidate)) {
                $candidate = "$baseId$suffix"
                $suffix++
            }

            $missing.Add([PSCustomObject]@{
                File = $file.FullName
                Line = $lineNumber
                Control = $controlType
                AutomationId = $candidate
                Text = ($tagText -split "`r?`n")[0].Trim()
            }) | Out-Null

            if ($Fix) {
                $insertIndex = $match.Index + $match.Value.IndexOf($match.Groups["attrs"].Value)
                $indent = ""
                if ($tagText -match "`r?`n(?<indent>\s*)[A-Za-z_][\w:\.]*\s*=") {
                    $indent = $Matches["indent"]
                }
                else {
                    $lineStart = $content.LastIndexOf("`n", [Math]::Max(0, $match.Index))
                    if ($lineStart -lt 0) {
                        $lineStart = 0
                    }
                    $currentLine = $content.Substring($lineStart, $match.Index - $lineStart)
                    $indent = ([regex]::Match($currentLine, "^\s*").Value) + "    "
                }

                $insertText = "`r`n$indent" + 'AutomationProperties.AutomationId="' + $candidate + '"'
                $replacements.Add([PSCustomObject]@{
                    Index = $insertIndex
                    Text = $insertText
                }) | Out-Null
            }
        }

        if ($Fix -and $replacements.Count -gt 0) {
            $builder = New-Object System.Text.StringBuilder $content
            foreach ($replacement in ($replacements | Sort-Object Index -Descending)) {
                [void]$builder.Insert($replacement.Index, $replacement.Text)
            }

            Set-Content -Path $file.FullName -Value $builder.ToString() -NoNewline
        }
    }

if ($missing.Count -gt 0) {
    $missing | Sort-Object File, Line | Format-Table -AutoSize

    if ($Fix) {
        Write-Host ""
        Write-Host "Added AutomationProperties.AutomationId to $($missing.Count) control(s)."
        exit 0
    }

    Write-Error "Missing AutomationProperties.AutomationId on $($missing.Count) control(s). Re-run with -Fix to add deterministic IDs."
}
else {
    Write-Host "All audited XAML controls have AutomationProperties.AutomationId."
}
