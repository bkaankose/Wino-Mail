# Linux release packages

The Linux app (`src/Wino.Mail.Linux`, issue #1216) is released as a **beta** for `amd64`/`x86_64` in three formats made
from one Native AOT publish: a Debian package, an Arch Linux package (also for Omarchy, and the source of the AUR package
`wino-mail-bin`) and a Fedora RPM. They are downloaded from the website, with an update check in the app. There is no
APT, pacman or DNF repository, no Flatpak and no arm64 build yet (see [Later](#later)). Beta builds get no GitHub
release and no tag, like Windows betas.

| Step | Script | Host | Credentials |
| --- | --- | --- | --- |
| Build and verify | `scripts/release/linux/build-linux-release.sh` | Ubuntu 26.04 (native or WSL2), amd64 | None |
| The Debian package | `scripts/release/linux/build-deb.sh` | Same | None |
| The Arch package | `scripts/release/linux/build-arch.sh` | Same (or Arch), as a normal user | None |
| The RPM | `scripts/release/linux/build-rpm.sh` | Same (or Fedora) | None |
| Upload | `scripts/release/linux/publish-linux-release.sh` | Same | `WINO_R2_*` (see [Local script environment](local-script-environment.md#publication-to-downloadwinomailapp-windows-beta-and-sideload-macos-dmg-linux-beta)) |
| AUR files | `scripts/release/linux/update-aur.sh` (run by the upload) | Same, as a normal user | None; the AUR push needs the owner's AUR SSH key |

## Supported systems

The app needs GTK 4.20 or later, libadwaita 1.8 or later, WebKitGTK 6.0 and glibc 2.38 or later (the newest symbol
version the Native AOT executable uses is `GLIBC_2.38`), on amd64/x86_64.

| System | Package | State |
| --- | --- | --- |
| Ubuntu 26.04 | `.deb` | Built and run (GTK 4.22, libadwaita 1.9) |
| Ubuntu 25.10, Debian forky (testing) | `.deb` | Supported by their package versions, not run |
| Arch Linux (current) | `.pkg.tar.zst`, AUR `wino-mail-bin` | Installed, `pacman -Qkk` clean, run under WSLg (GTK 4.24, libadwaita 1.10, OpenSSL 4.1) |
| Omarchy 4.5 | `.pkg.tar.zst`, AUR `wino-mail-bin` | Installed, verified and removed in a VM |
| Fedora 44 | `.rpm` | Installed with `dnf`, `rpm -V` clean, run under WSLg (GTK 4.22, libadwaita 1.9, glibc 2.43) |
| Fedora 43, Fedora 45 | `.rpm` | Supported by their package versions (Fedora 43: GTK 4.20, libadwaita 1.8, glibc 2.42), not run |

Fedora 42 and older lack GTK 4.20 and libadwaita 1.8. The .NET runtime in the executable loads OpenSSL 3 or 4 and the
system's ICU by name, so new OpenSSL and ICU releases need no rebuild.

## One-time setup

Install the tools of the [Linux development guide](harness/development-linux.md#requirements) and the packaging tools:

```sh
sudo apt install clang zlib1g-dev dpkg-dev fakeroot lintian jq appstream desktop-file-utils curl git \
  makepkg pacman-package-manager rpm rpmlint libarchive-tools zstd
```

`makepkg` (Ubuntu's package of pacman's build tool) refuses to run as root, so build as a normal user. `namcap` exists
only on Arch: on Ubuntu the Arch package is not linted (`build.json` records `"not run"`); lint it on Arch with
`namcap <package>` when its contents change.

Install the .NET SDK that `global.json` selects into `~/.dotnet` (or on `PATH`). `lintian` is optional for the scripts,
but a release should be linted: without it the build prints that the package was not linted and records
`"lintian": "not run"` in `build.json`.

## Build

```sh
bash scripts/release/linux/build-linux-release.sh
```

It asks whether to translate missing resources first (as the macOS scripts do; `--translate`, `--skip-translation`,
`--non-interactive`), then runs one Native AOT publish (`-p:WinoLinuxPublish=Aot`, never `-p:PublishAot`), stages the
package tree once (`lib/stage.sh`), runs `build-deb.sh`, `build-arch.sh` and `build-rpm.sh` on that tree, writes the
update feed and the build record, and checks its own output. Options: `--build-number <n>` (default: the UTC time as
`yyDDDHHMM`, as on macOS), `--output-root <path>`, `--only deb,arch,rpm` or `--skip <kinds>` (a missing tool of a
selected format stops the preflight), `--publish-dir <dir>` (package an existing publish), `--skip-lintian`,
`--skip-rpmlint`. It needs no credentials and no network beyond NuGet, and it stops when the build folder exists.

`--from-deb <deb>` repackages a beta `.deb` that is already built: it keeps the package's version, build and file,
extracts its tree, builds the Arch package and the RPM from it, and writes `release.json`, `build.json` (commit and
lintian result taken from the old `build.json`) and `SHA256SUMS` again. Pointed at the `.deb` in its own build folder,
it adds the two packages to that folder; if it fails there, the folder gets its old feed, record and checksums back.
Each packaging script also runs alone with `--publish-dir`, `--root` (a tree staged by `build-linux-release.sh`) or
`--from-deb`.

The version is the Windows manifest version: `2.2.15.0` becomes app version `2.2.15` (`Wino.Mail.Linux.csproj` reads
it, as Android does), Debian and RPM version **`2.2.15~beta<build>`** (RPM release `1`, no `%{?dist}`: one package for
every Fedora) and Arch version **`2.2.15beta<build>-1`** (pacman allows no `~`). In all three a beta sorts before the
final version and a newer build after an older one: `vercmp 2.2.15beta2 2.2.15` and `rpm.vercmp("2.2.15~beta2",
"2.2.15")` both give `-1`.

### Output

`${WINO_RELEASES_ROOT:-~/Wino Releases}/<a.b.c.0>/Linux/<build>/`:

```text
wino-mail_<a.b.c>~beta<build>_amd64.deb              the Debian package
wino-mail-<a.b.c>beta<build>-1-x86_64.pkg.tar.zst     the Arch package
wino-mail-<a.b.c>~beta<build>-1.x86_64.rpm            the RPM
<each package>.sha256
release.json      the update feed, uploaded as linux/beta/release.json
build.json        version, build, channel, commit and dirty flag, deb/debVersion, and per package its file, version
                  and lint result (packages.deb, packages.arch, packages.rpm)
SHA256SUMS        the packages and both JSON files
Symbols/amd64/    wino-mail.dbg and the *.pdb files (not packaged)
publish.log, package.log, package-arch.log, package-rpm.log
```

### The packages

All three install the same files; only `distribution` names its format. The Debian package:

```text
/usr/lib/wino-mail/      wino-mail, the four native libraries, Fonts/, Models/, Themes/, Assets/, icons/, distribution
/usr/bin/wino-mail       -> ../lib/wino-mail/wino-mail
/usr/share/applications/app.winomail.Mail.desktop
/usr/share/icons/hicolor/<size>/apps/app.winomail.Mail.png
/usr/share/metainfo/app.winomail.Mail.metainfo.xml (with a <release type="development"> entry)
/usr/share/man/man1/wino-mail.1.gz, /usr/share/doc/wino-mail/{copyright,changelog.gz}
/usr/share/lintian/overrides/wino-mail
```

The data stays beside the executable because the app reads it from `AppContext.BaseDirectory`, which Native AOT
resolves through the `/usr/bin` link. `dpkg-shlibdeps` finds only libc, libgcc and libstdc++ (GTK, libadwaita,
WebKitGTK, libsecret, libcanberra, ICU and OpenSSL are loaded by name at run time), so the rest of `Depends` is in
`scripts/release/linux/deb/control.in`. There are no maintainer scripts: the triggers of `desktop-file-utils`,
`hicolor-icon-theme` and AppStream refresh the caches. Removing the package keeps the user's data in
`~/.local/share/wino-mail` and the keyring item.

The text file `/usr/lib/wino-mail/distribution` (`channel=beta`, `package=deb|arch|rpm`, `build=<n>`) marks a packaged
copy. Without it a build is a development build: About shows no "(Beta)", there is no update check, and telemetry is
off.

The **Arch package** (`build-arch.sh`, `arch/PKGBUILD.in`, `arch/makepkg.conf`) is made by `makepkg` from a generated
PKGBUILD that copies the staged tree; `arch/makepkg.conf` is a complete configuration, so neither the host's
`/etc/makepkg.conf` nor a user's `makepkg.conf` changes the result. `options=('!strip' '!debug' ...)` keeps the
executable byte for byte (the script checks its SHA-256), the package is zstd level 19, and it has no install script:
pacman's hooks refresh the icon and desktop caches. Instead of `/usr/share/doc` and the lintian overrides it carries
`/usr/share/licenses/wino-mail/` (`LICENSE`, the copyright file, plus `MIT` and `BSD-3-Clause`, which namcap requires).
`depends` lists the libraries loaded by name (gtk4, libadwaita, webkitgtk-6.0, libsecret, libcanberra, icu, openssl,
glibc, gcc-libs, ca-certificates, hicolor-icon-theme); gnome-keyring, sounds, the portal, fonts, Enchant, the English
dictionary and krb5 are `optdepends`. A Secret Service with an unlocked default collection is still required at run
time; Omarchy has gnome-keyring.

The **RPM** (`build-rpm.sh`, `rpm/wino-mail.spec.in`) is made by `rpmbuild` with stripping, debug packages, build-id
links and `brp` post-processing turned off, `AutoReqProv: no` (the bundled libraries must not become Provides of the
system), RPM v4 format (`_rpmformat 4`; rpm 6 writes v6 by default) and a zstd level 19 payload. `Requires` names
the same libraries by Fedora package (`gtk4 >= 4.20`, `libadwaita >= 1.8`, `webkitgtk6.0`, `libsecret`, `libcanberra`,
`libicu`, `openssl-libs`, `glibc >= 2.38`, `libstdc++`, `libgcc`, `ca-certificates`, `hicolor-icon-theme`);
`Recommends` and `Suggests` the optional ones. rpmlint findings are checked against `rpm/rpmlint-accepted`
(`no-signature`, `explicit-lib-dependency`, and the openSUSE defaults of Ubuntu's rpmlint); any other finding fails the
build, on Ubuntu and on Fedora alike.

## Telemetry

A packaged Release build reports crashes and errors to Sentry exactly as the other platforms' release builds do: the
same address (from `ApplicationConfiguration` in `Wino.Services`), environment `production`, release
`wino-mail@<a.b.c>`, the package's build number as dist, the same scrubbing, and the same Privacy setting that turns it
off. `LinuxPlatformRegistration` passes the address only when `distribution` exists and the build is Release; Debug
builds and plain publishes keep it empty. Nothing has to be supplied at release time. Symbol upload to Sentry is not
automated on Linux; `Symbols/amd64` keeps what `sentry debug-files upload` would need.

## Publishing

```sh
bash scripts/release/linux/publish-linux-release.sh --version 2.2.15.0 [--build <n>] [--dry-run]
```

The Linux twin of `publish-macos-release.sh`, with the same options (`--version`, `--build`, `--output-root`,
`--dry-run`, `--allow-older`, `--non-interactive`, plus `--skip-aur`) and the same upload code: `curl --aws-sigv4`
against R2's S3 API, the credentials on stdin, never on the command line. Without `--dry-run` it refuses to start unless
`WINO_R2_ACCOUNT_ID`, `WINO_R2_ACCESS_KEY_ID` and `WINO_R2_SECRET_ACCESS_KEY` are set. It publishes the packages the
feed lists (a feed with only the `.deb` stays valid) and checks for each that `release.json` describes it (name, size,
SHA-256, address), that its `.sha256` matches, and that the package itself carries the feed's version and build
(`dpkg-deb -f`, `.PKGINFO` read with `bsdtar`, `rpm -qp`; each tool is needed only when its format is in the feed), plus
the feed's channel and links and that the live feed is not newer (`--allow-older` overrides). Then it uploads, in this
order, to the `wino-downloads` bucket:

1. For the `.deb`, then the Arch package, then the RPM: the versioned file (then checks the public size), its
   `.sha256`, and the alias the website links to (as `macos/WinoMail.dmg`): `wino-mail_amd64.deb`,
   `wino-mail-x86_64.pkg.tar.zst`, `wino-mail.x86_64.rpm`
2. `linux/beta/release.json`, last, and downloads it again to confirm the build
3. With an Arch package in the feed: `update-aur.sh` (below)

`--dry-run` runs every check and lists the uploads without credentials, and leaves the AUR files alone. Each package is
far below R2's single-request limit, so there is no multipart upload.

### The AUR package

`scripts/release/linux/aur/wino-mail-bin/` holds the AUR package `wino-mail-bin`: a PKGBUILD that downloads the
published Arch package, checks its SHA-256 and installs its files (`provides` and `conflicts` `wino-mail`, the same
dependencies; its licenses move to `/usr/share/licenses/wino-mail-bin`), and its `.SRCINFO`. After an upload,
`update-aur.sh --version <a.b.c.0> --build <n>` rewrites `pkgver` and `sha256sums`, resets `pkgrel` to 1 and regenerates
`.SRCINFO` with `makepkg --printsrcinfo`, then prints the owner's steps. Commit the two files in this repository, then:

```sh
git clone ssh://aur@aur.archlinux.org/wino-mail-bin.git ~/aur-wino-mail-bin   # once (the first push creates it)
cp scripts/release/linux/aur/wino-mail-bin/{PKGBUILD,.SRCINFO} ~/aur-wino-mail-bin/
git -C ~/aur-wino-mail-bin add PKGBUILD .SRCINFO
git -C ~/aur-wino-mail-bin commit -m "Update to <a.b.c>beta<build>"
git -C ~/aur-wino-mail-bin push
```

The AUR account and its SSH key are the owner's; no script pushes to the AUR.

### From the Windows PC (WSL2)

Native AOT cannot build `linux-x64` on Windows, so the Windows PC uses the same bash scripts in WSL2:

1. In WSL2 Ubuntu 26.04, install the tools above. Clone the repository inside the Linux file system (not under
   `/mnt/c`: slow, and it loses executable bits and line endings) and pull the working branch.
2. `bash scripts/release/linux/build-linux-release.sh`. Keep the default output root in the WSL home; Windows sees it as
   `\\wsl.localhost\<distro>\home\<user>\Wino Releases`.
3. Upload from WSL with the Windows environment variables passed through `WSLENV`, so they are not stored in WSL:

   ```powershell
   $env:WSLENV = 'WINO_R2_ACCOUNT_ID:WINO_R2_ACCESS_KEY_ID:WINO_R2_SECRET_ACCESS_KEY'
   wsl -d <distro> -- bash ~/src/Wino-Mail/scripts/release/linux/publish-linux-release.sh --version 2.2.15.0
   ```

4. Before publishing, install the package once yourself: inside WSL (WSLg shows the window) or on a real Ubuntu.

## The update feed and the in-app check

`release.json` (schema 1), one entry per format under `packages`:

```json
{
  "schema": 1, "platform": "linux", "channel": "beta", "version": "2.2.15", "build": 263261530,
  "publishedUtc": "2026-11-22T15:30:00Z",
  "notesUrl": "https://www.winomail.app", "downloadPageUrl": "https://www.winomail.app",
  "packages": {
    "deb":  { "amd64":  { "url": "https://download.winomail.app/linux/beta/wino-mail_2.2.15~beta263261530_amd64.deb",
                          "sha256": "…", "size": 55402748 } },
    "arch": { "x86_64": { "url": "https://download.winomail.app/linux/beta/wino-mail-2.2.15beta263261530-1-x86_64.pkg.tar.zst",
                          "sha256": "…", "size": 59588940 } },
    "rpm":  { "x86_64": { "url": "https://download.winomail.app/linux/beta/wino-mail-2.2.15~beta263261530-1.x86_64.rpm",
                          "sha256": "…", "size": 59638759 } }
  }
}
```

A copy reads only the entry of its own `package=` (`LinuxPackageKind`); `.deb` betas released before the Arch and RPM
packages read `packages.deb` and ignore the rest, so they keep updating.

`notesUrl` and `downloadPageUrl` are the website (`AppUrls.Website`), because neither the macOS appcast nor the website
has a release-notes or Linux download page yet. Change `NOTES_URL` and `DOWNLOAD_PAGE_URL` in
`scripts/release/linux/lib/common.sh` when such a page exists; the app opens only `https` addresses on
`download.winomail.app`, `winomail.app` and `www.winomail.app`.

A packaged copy (`Infrastructure/Updates/LinuxUpdateService`) reads `https://download.winomail.app/linux/beta/release.json`
60 seconds after start when the last check is older than 24 hours, and re-tests that rule every hour while it runs.
Settings › General has **Check for updates automatically** with **Check Now**; the primary menu has **Check for
Updates…**. A newer `(version, build)` shows **Update available** once per build. For a `.deb` or `.rpm` copy,
**Update** downloads the package into the Downloads folder, requires the feed's size and SHA-256, and opens the file
with the desktop's handler for it (App Center on Ubuntu, Software on Fedora), which asks for the password and upgrades
in place; the new version starts with the next launch. A failed download or launch opens the package address in the
browser and shows "The update couldn't be installed". No desktop installs Arch packages, so an Arch copy is only told:
**Update** opens `downloadPageUrl` (the package address when the feed has none), and the user updates with pacman or the
AUR helper. A copy with an unknown `package=` has no update check. The requests carry no identifier, cookie or locale;
the User-Agent is `WinoMail-Linux/<version>`.

The packages are not signed in the beta: integrity rests on HTTPS to the fixed host and the feed's SHA-256 (the AUR
PKGBUILD checks the same SHA-256). Signed repositories (see [Later](#later)) add signatures.

## What users do

- **Ubuntu and Debian:** download `https://download.winomail.app/linux/beta/wino-mail_amd64.deb`, open it (App Center
  installs it) or run `sudo apt install ./wino-mail_amd64.deb`. Later builds: the app offers them.
- **Fedora:** `sudo dnf install https://download.winomail.app/linux/beta/wino-mail.x86_64.rpm`, or download it and open
  it in Software. dnf installs an unsigned package named on its command line. Later builds: the app offers them.
- **Arch Linux and Omarchy:** with an AUR helper, `yay -S wino-mail-bin` (or `paru`); updates come with the helper's
  system upgrade. Without one:

  ```sh
  curl -LO https://download.winomail.app/linux/beta/wino-mail-x86_64.pkg.tar.zst
  sudo pacman -U ./wino-mail-x86_64.pkg.tar.zst
  ```

  `sudo pacman -U <address>` does not work: the stock `pacman.conf` (Arch and Omarchy alike) requires a signature for
  a remote package and fails on the missing `.sig`, while a local file is accepted (`LocalFileSigLevel = Optional`).
  Later builds: the app says when one exists; run the same two commands.

## Owner test checklist

Arch, Omarchy and Fedora: install with the commands above (the AUR package after its first push), start from the
launcher, About shows `2.2.15 (Beta)`, Check for Updates offers a newer build (Arch: opens the download page), and
`sudo pacman -R wino-mail` or `sudo dnf remove wino-mail` leaves no package files and keeps the data.

Ubuntu:

On a clean Ubuntu 26.04 before the first publish: install by double click; the launcher shows the icon and "Wino Mail";
About shows `2.2.15 (Beta)`; notifications carry the app name; `wino://` and `mailto:` open the app (after choosing it
as the mail app); `.eml` files offer Open With Wino Mail; an older beta offered the new build updates through App
Center; `sudo apt remove wino-mail` leaves no package files and keeps the data.

## Later

- **arm64**: cross-compile from Linux with an arm64 sysroot (`clang --target=aarch64-linux-gnu`, `-r linux-arm64`) for a
  build check; run the package on an Ubuntu 26.04 arm64 VM on the owner's Mac mini. `build-deb.sh` builds only for its
  host architecture today.
- **Signed APT repository** (`download.winomail.app/linux/apt`, by-hash, `InRelease` uploaded last, an owner-held GPG
  key): the package installs its own `.sources` file and keyring so Software Updater delivers updates, and the in-app
  dialog only points at it.
- **Signed pacman repository** (`download.winomail.app/linux/arch`, a `repo-add` database and detached `.sig` files
  signed with an owner-held key, the key in a `wino-keyring` package): `pacman -Syu` then updates Wino with the system,
  and `pacman -U <address>` works too.
- **Signed DNF repository** (`download.winomail.app/linux/rpm`, `createrepo_c` metadata, packages signed with
  `rpmsign`, a `.repo` file shipped in a `wino-release` package): `dnf upgrade` delivers updates.
- **Flatpak** on `org.gnome.Platform`, first from an own repository, then Flathub.
- A stable channel (`linux/release.json`, `linux/*.deb`); beta copies keep reading `linux/beta/`.
