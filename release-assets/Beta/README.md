# Beta artwork

Place replacement artwork here, with the same paths as the application assets.
You can also supply another directory with `-BetaAssetsPath`.

The required set includes packaged app-entry PNG/SVG files, Store logos, `WinoLogo.png`, `Wino_Icon.ico`, and `EML/eml.png`.
Keep image dimensions, scale variants, target-size variants, and transparent backgrounds consistent with the originals.
The packager requires all branding assets present in its compiled payload. It never substitutes stable artwork for a missing beta asset.

To list candidate source paths, run this command from the repository root:

```powershell
. ./scripts/build-releases.ps1
Get-ReleaseBrandingPaths ./src/Wino.Mail.WinUI
```

The source inventory can include variants excluded from packaging. The compiled payload determines the required set.
Place files such as `Assets/Wino_Icon.ico` under this directory. Do not place EXE or DLL files here.
Artwork changes do not require recompilation. They do require new packages and signatures.
