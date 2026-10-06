# Wino AppKit infrastructure PoC

This standalone project tests the proposed native AppKit presentation boundary without adding it to `WinoMail.slnx` or changing the WinUI application. Its controller/ViewModel names follow the intended convention: `AboutPageViewController` pairs with `AboutPageViewModel`, matching the existing `AboutPage.xaml` screen. The sample ViewModel lives in its own plain `net10.0` project and depends only on Wino's shared ViewModel/domain contracts plus a platform-neutral dialog interface; it has no AppKit reference.

The sample covers native window startup, AppKit dialog presentation, `Translator` lookup from the shared domain library, `CoreBaseViewModel` dispatcher and navigation lifecycle hooks (`OnNavigatedTo`, `OnNavigatedFrom`, and `OnPageLoaded`), `[ObservableProperty]` and `[RelayCommand]`, typed text/command binding helpers, and deterministic controller/ViewModel cleanup. The Navigate away / Return button exercises navigation lifecycle callbacks; closing the window exercises teardown and disposal.

Build and run on the Mac host with the .NET macOS workload and Xcode command-line tools installed:

```sh
dotnet run --project src/Wino.Mail.AppKit.Poc/Wino.Mail.AppKit.Poc.csproj
```

This PoC is intentionally not included in the Windows solution build because native AppKit targeting requires the macOS workload. It must be built and smoke-tested on the Mac host before its infrastructure decisions are treated as validated.
