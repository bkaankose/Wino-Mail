param(
    [string[]]$Root = @("src", "controls")
)

# Fails when icons bypass the WinoIcons fonts generated from icons/manifest.json.
# App XAML uses WinoFontIcon Icon="Name"; the controls library uses its WinoFontIcon with WinoIconCodes.
# See icons/README.md for adding an icon.

$ErrorActionPreference = "Stop"

# Allowed on purpose: the copied InfoBar template keeps the platform close symbol.
$allowList = @(
    @{ File = "Styles\WinoInfoBar.xaml"; Pattern = "InfoBarCloseButtonSymbol" }
)

# Elements whose Icon, HeaderIcon, ActionIcon or IconSource property is an IconElement or IconSource,
# so Icon="Name" there silently creates a SymbolIcon. Other controls may declare their own typed Icon.
$iconHosts = @(
    "AppBarButton", "AppBarToggleButton", "NavigationViewItem", "WinoNavigationViewItem", "MenuFlyoutItem",
    "MenuFlyoutSubItem", "ToggleMenuFlyoutItem", "RadioMenuFlyoutItem", "SelectorBarItem", "SettingsCard",
    "SettingsExpander", "SwipeItem", "TabViewItem", "InfoBar", "TeachingTip", "XamlUICommand", "StandardUICommand",
    "SegmentedItem", "SplitButton", "TokenItem"
)

$xamlRules = @(
    @{ Name = "SymbolIcon"; Pattern = "<(?:[A-Za-z_]\w*:)?SymbolIcon(?:Source)?\b" },
    @{ Name = "PathIcon"; Pattern = "<(?:[A-Za-z_]\w*:)?PathIcon(?:Source)?\b" },
    @{ Name = "FontIcon with a glyph"; Pattern = "<(?:[A-Za-z_]\w*:)?FontIcon(?:Source)?\b[^>]*\bGlyph\s*=" },
    @{ Name = "Symbol shorthand"; Pattern = '(?<![\w.:])(?:Icon|ActionIcon|HeaderIcon|IconSource)\s*=\s*"[A-Z][A-Za-z]+"' }
)

$codeRules = @(
    @{ Name = "Platform icon in code"; Pattern = "\bnew\s+(?:SymbolIcon|SymbolIconSource|PathIcon|FontIcon)\s*[\(\{]" },
    @{ Name = "Symbol enum"; Pattern = "\bSymbol\.[A-Z]\w+" },
    @{ Name = "Glyph literal"; Pattern = '"\\u[EF][0-9A-Fa-f]{3}"|"[\uE000-\uF8FF]"' }
)

function Test-Allowed {
    param([string]$RelativePath, [string]$Text)

    foreach ($entry in $allowList) {
        if ($RelativePath.EndsWith($entry.File, [System.StringComparison]::OrdinalIgnoreCase) -and $Text -match $entry.Pattern) {
            return $true
        }
    }

    return $false
}

function Find-Violations {
    param([System.IO.FileInfo]$File, [string]$RootPath, [array]$Rules, [bool]$IsXaml)

    $content = Get-Content -Path $File.FullName -Raw -Encoding UTF8
    if ([string]::IsNullOrEmpty($content)) {
        return
    }

    # Substring instead of Path.GetRelativePath so the script also runs on Windows PowerShell 5.1.
    $relativePath = $File.FullName.Substring($RootPath.Length).TrimStart('\', '/')
    foreach ($rule in $Rules) {
        # (?s) lets XAML attributes span lines; element tags never contain '>' before they close.
        $pattern = if ($IsXaml) { "(?s)" + $rule.Pattern } else { $rule.Pattern }
        foreach ($match in [regex]::Matches($content, $pattern)) {
            $tagEnd = $content.IndexOf(">", $match.Index)
            $tagText = if ($tagEnd -gt $match.Index) { $content.Substring($match.Index, $tagEnd - $match.Index) } else { $match.Value }

            # Shorthand only matters on controls whose icon property is an IconElement or IconSource.
            if ($rule.Name -eq "Symbol shorthand") {
                $tagStart = $content.LastIndexOf("<", $match.Index)
                $hostType = ""
                if ($tagStart -ge 0 -and $content.Substring($tagStart, $match.Index - $tagStart) -match "^<(?:[A-Za-z_]\w*:)?(?<type>\w+)") {
                    $hostType = $Matches.type
                }
                if ($iconHosts -notcontains $hostType) {
                    continue
                }
            }

            if (Test-Allowed -RelativePath $relativePath -Text $tagText) {
                continue
            }

            $line = ($content.Substring(0, $match.Index) -split "`n").Count
            [PSCustomObject]@{
                File = $relativePath
                Line = $line
                Rule = $rule.Name
                Text = ($match.Value -replace "\s+", " ").Trim()
            }
        }
    }
}

# The app WinoFontIcon gets its FontFamily from an implicit style. Inside a ControlTemplate of a
# style dictionary that style is not applied, so the icon falls back to Segoe Fluent Icons and draws
# whatever Segoe has at the Wino codepoint. Such icons must set FontFamily themselves.
$appIconNamespace = "using:Wino.Mail.WinUI.Controls"

function Find-TemplateIconViolations {
    param([System.IO.FileInfo]$File, [string]$RootPath)

    try {
        $document = [System.Xml.Linq.XDocument]::Load($File.FullName, [System.Xml.Linq.LoadOptions]::SetLineInfo)
    }
    catch {
        return
    }

    $relativePath = $File.FullName.Substring($RootPath.Length).TrimStart('\', '/')
    foreach ($element in $document.Descendants()) {
        if ($element.Name.LocalName -notin @("WinoFontIcon", "WinoFontIconSource") -or $element.Name.NamespaceName -ne $appIconNamespace) {
            continue
        }
        if ($null -ne $element.Attribute("FontFamily")) {
            continue
        }
        if (-not ($element.Ancestors() | Where-Object { $_.Name.LocalName -eq "ControlTemplate" } | Select-Object -First 1)) {
            continue
        }

        [PSCustomObject]@{
            File = $relativePath
            Line = ([System.Xml.IXmlLineInfo]$element).LineNumber
            Rule = "WinoFontIcon in ControlTemplate without FontFamily"
            Text = 'Add FontFamily="{ThemeResource WinoIconFontFamily}"'
        }
    }
}

$excluded = '\\(bin|obj|AppPackages)\\'
$violations = [System.Collections.Generic.List[object]]::new()

foreach ($folder in $Root) {
    $rootPath = (Resolve-Path $folder).Path

    Get-ChildItem -Path $rootPath -Recurse -File -Include *.xaml |
        Where-Object { $_.FullName -notmatch $excluded } |
        ForEach-Object {
            Find-Violations -File $_ -RootPath $rootPath -Rules $xamlRules -IsXaml $true
            Find-TemplateIconViolations -File $_ -RootPath $rootPath
        } |
        ForEach-Object { $violations.Add($_) }

    Get-ChildItem -Path $rootPath -Recurse -File -Include *.cs |
        Where-Object { $_.FullName -notmatch $excluded -and $_.Name -notlike "*.g.cs" } |
        ForEach-Object { Find-Violations -File $_ -RootPath $rootPath -Rules $codeRules -IsXaml $false } |
        ForEach-Object { $violations.Add($_) }
}

if ($violations.Count -gt 0) {
    $violations | Sort-Object File, Line | Format-Table -AutoSize -Wrap
    Write-Error "Found $($violations.Count) icon(s) that bypass the WinoIcons fonts. Add the icon with icons/tools/add_fluent_icon.py and use WinoFontIcon instead."
}
else {
    Write-Host "All icons use the WinoIcons fonts."
}
