; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
WINOICON001 | WinoIcons | Error | WinoIconGenerator, invalid icons/manifest.json
WINOAOT001 | WinoAot | Error | ItemsSourceStaticTypeAnalyzer, items source reached through an interface or object static type
WINOMSG001 | WinoMessaging | Error | GlobalMessengerAnalyzer, process-wide messenger used in shared code
WINOUI001 | WinoLayering | Error | BackendUiContractAnalyzer, dialog, navigation, theme or dispatcher contract taken below the ViewModels
