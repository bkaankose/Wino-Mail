"""Import the platform icon sets from their pinned npm packages.

    python icons/tools/import_icon_sets.py            # write icons/sets/<Set>.json
    python icons/tools/import_icon_sets.py --check    # exit 1 if the committed set files are stale

icons/sets.json maps every Wino icon (except brand marks) to a glyph name in each source library. This
script reads those glyphs from the package tarballs, maps each library's grid onto the WinoIcons em and
writes one file per set with em-space path data:

  "glyph"   the outline, or the filled glyph for icons marked "filled"
  "accent"  the filled glyph, for icons with an accent in manifest.json whose filled glyph differs; the
            colorful fonts draw it in the palette color under "glyph"

Tarballs are downloaded once into icons/.cache and checked against the integrity hash in sets.json.
Run build_fonts.py afterwards.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import sys
import tarfile
import urllib.request
import xml.etree.ElementTree as ET

from wino_icons import ICONS_DIR, REPO_ROOT, SETS_DIR, load_manifest, load_sets, union_path_data

CACHE_DIR = ICONS_DIR / ".cache"


def tarball(source: dict) -> tarfile.TarFile:
    package, version = source["package"], source["version"]
    stem = package.split("/")[-1]
    cached = CACHE_DIR / f"{package.replace('/', '__')}-{version}.tgz"
    if not cached.is_file():
        url = f"https://registry.npmjs.org/{package}/-/{stem}-{version}.tgz"
        try:
            with urllib.request.urlopen(url) as response:
                data = response.read()
        except OSError as error:
            raise SystemExit(f"could not download {url}: {error}")
        cached.parent.mkdir(parents=True, exist_ok=True)
        cached.write_bytes(data)
    data = cached.read_bytes()
    algorithm, expected = source["integrity"].split("-", 1)
    actual = base64.b64encode(hashlib.new(algorithm, data).digest()).decode()
    if actual != expected:
        cached.unlink()
        raise SystemExit(f"{package}@{version}: integrity mismatch, the download was removed")
    return tarfile.open(fileobj=io.BytesIO(data), mode="r:gz")


class Library:
    """Reads one source library's SVGs and maps its grid onto the em (y down, 0 at the ascent line)."""

    def __init__(self, key: str, source: dict, upm: int):
        self.key = key
        self.archive = tarball(source)
        self.members = {m.name: m for m in self.archive.getmembers() if m.isfile()}
        self.upm = upm

    def read(self, path: str) -> ET.Element | None:
        member = self.members.get(f"package/{path}")
        if member is None:
            return None
        return ET.fromstring(self.archive.extractfile(member).read())

    def files(self, name: str, style: str | None) -> tuple[str, str]:
        if self.key == "material":
            return f"{style}/{name}.svg", f"{style}/{name}-fill.svg"
        if name.endswith("_fill"):
            # Some Framework7 glyphs exist only filled; they are their own outline.
            return f"svg/{name}.svg", f"svg/{name}.svg"
        return f"svg/{name}.svg", f"svg/{name}_fill.svg"

    def transform(self, root: ET.Element) -> tuple:
        x, y, w, h = (float(v) for v in root.get("viewBox").split())
        if self.key == "framework7":
            # Framework7 draws edge to edge on its 56 grid; pad it to the live area the other sets use.
            x, y, w, h = x - 5, y - 5, w + 10, h + 10
        scale = self.upm / w
        return (scale, 0, 0, scale, -x * scale, -y * scale)

    def glyph(self, path: str, label: str) -> str | None:
        root = self.read(path)
        return None if root is None else union_path_data(root, self.transform(root), label)


def import_set(set_name: str, spec: dict, library: Library, sets: dict, manifest: dict) -> dict:
    accents = {i["name"] for i in manifest["icons"] if "accent" in i}
    out, missing = {}, []
    for name, mapping in sets["icons"].items():
        glyph_name = mapping[spec["source"]]
        outline_file, filled_file = library.files(glyph_name, spec.get("style"))
        outline = library.glyph(outline_file, f"{set_name}/{name}")
        filled = library.glyph(filled_file, f"{set_name}/{name}")
        if outline is None:
            missing.append(f"{name} ({outline_file})")
            continue
        entry = {"glyph": filled if mapping.get("filled") and filled else outline}
        if name in accents and not mapping.get("filled") and filled and filled != outline:
            entry["accent"] = filled
        out[name] = entry
    if missing:
        raise SystemExit(f"{set_name}: no glyph for " + ", ".join(missing))
    return out


def render(data: dict) -> str:
    # One icon per line keeps diffs readable.
    lines = [f"  {json.dumps(name)}: {json.dumps(entry, separators=(', ', ': '))}" for name, entry in data.items()]
    return "{\n" + ",\n".join(lines) + "\n}\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="fail if the committed set files differ from a fresh import")
    args = parser.parse_args()

    manifest, sets = load_manifest(), load_sets()
    names = {i["name"] for i in manifest["icons"]}
    unknown = [n for n in sets["icons"] if n not in names]
    if unknown:
        raise SystemExit("sets.json names not in manifest.json: " + ", ".join(unknown))

    libraries: dict[str, Library] = {}
    stale = []
    for set_name, spec in sets["sets"].items():
        key = spec["source"]
        if key not in libraries:
            libraries[key] = Library(key, sets["sources"][key], manifest["unitsPerEm"])
        text = render(import_set(set_name, spec, libraries[key], sets, manifest))
        path = SETS_DIR / f"{set_name}.json"
        if args.check:
            if not path.is_file() or path.read_text(encoding="utf-8") != text:
                stale.append(str(path.relative_to(REPO_ROOT)))
            continue
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="\n")
        print(f"wrote {path.relative_to(REPO_ROOT)} ({len(text)} bytes)")

    if args.check:
        if stale:
            print("stale icon sets (run icons/tools/import_icon_sets.py): " + ", ".join(stale))
            return 1
        print("icon sets are up to date")
    return 0


if __name__ == "__main__":
    sys.exit(main())
