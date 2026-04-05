#!/usr/bin/env python3
"""
One-off maintenance script for syncing translated Wino `resources.json` files
with `en_US/resources.json` and bulk-translating missing keys via the OpenAI API.

By default this script runs in dry-run mode and only reports the planned changes.
Use `--apply` to write updates.

Examples:
  python scripts/translate_resources.py --dry-run
  python scripts/translate_resources.py --apply --model gpt-5-nano
  python scripts/translate_resources.py --apply --locales pl_PL de_DE --chunk-size 120


Usage:
    $env:OPENAI_API_KEY="{open ai key here}"
    python .\scripts\translate_resources.py --dry-run
    python .\scripts\translate_resources.py --apply --model gpt-5-nano --workers 4
"""

from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
import sys
import time
import urllib.error
import urllib.request
from collections import OrderedDict
from pathlib import Path
from typing import Dict, Iterable, List, Sequence, Tuple


LOCALE_LABELS = {
    "bg_BG": "Bulgarian (Bulgaria)",
    "ca_ES": "Catalan (Spain)",
    "cs_CZ": "Czech (Czech Republic)",
    "da_DK": "Danish (Denmark)",
    "de_DE": "German (Germany)",
    "el_GR": "Greek (Greece)",
    "es_ES": "Spanish (Spain)",
    "fi_FI": "Finnish (Finland)",
    "fr_FR": "French (France)",
    "gl_ES": "Galician (Spain)",
    "id_ID": "Indonesian (Indonesia)",
    "it_IT": "Italian (Italy)",
    "ja_JP": "Japanese (Japan)",
    "lt_LT": "Lithuanian (Lithuania)",
    "nl_NL": "Dutch (Netherlands)",
    "pl_PL": "Polish (Poland)",
    "pt_BR": "Portuguese (Brazil)",
    "ro_RO": "Romanian (Romania)",
    "ru_RU": "Russian (Russia)",
    "sk_SK": "Slovak (Slovakia)",
    "tr_TR": "Turkish (Turkey)",
    "uk_UA": "Ukrainian (Ukraine)",
    "zh_CN": "Chinese, Simplified (China)",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Bulk-sync and translate Wino resources.json files.")
    parser.add_argument(
        "--translations-root",
        default=str(Path("Wino.Core.Domain") / "Translations"),
        help="Path to the translations root directory.",
    )
    parser.add_argument(
        "--source-locale",
        default="en_US",
        help="Source locale directory name.",
    )
    parser.add_argument(
        "--locales",
        nargs="*",
        help="Specific locale directory names to process. Defaults to all non-source locales.",
    )
    parser.add_argument(
        "--model",
        default="gpt-5-nano",
        help="OpenAI model name to use for translation.",
    )
    parser.add_argument(
        "--chunk-size",
        type=int,
        default=100,
        help="How many keys to translate per API request.",
    )
    parser.add_argument(
        "--workers",
        type=int,
        default=4,
        help="How many locales to process in parallel.",
    )
    parser.add_argument(
        "--max-retries",
        type=int,
        default=4,
        help="Maximum retries per translation chunk.",
    )
    parser.add_argument(
        "--api-key-env",
        default="OPENAI_API_KEY",
        help="Environment variable that stores the OpenAI API key.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Report planned changes without writing files.",
    )
    parser.add_argument(
        "--apply",
        action="store_true",
        help="Write the translated files back to disk.",
    )
    args = parser.parse_args()
    if args.apply == args.dry_run:
        parser.error("Choose exactly one of --apply or --dry-run.")
    if args.chunk_size < 1:
        parser.error("--chunk-size must be greater than 0.")
    if args.workers < 1:
        parser.error("--workers must be greater than 0.")
    if args.max_retries < 1:
        parser.error("--max-retries must be greater than 0.")
    return args


def read_json(path: Path) -> OrderedDict[str, str]:
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        return json.load(handle, object_pairs_hook=OrderedDict)


def has_utf8_bom(path: Path) -> bool:
    return path.read_bytes().startswith(b"\xef\xbb\xbf")


def write_json(path: Path, data: OrderedDict[str, str], include_bom: bool) -> None:
    encoding = "utf-8-sig" if include_bom else "utf-8"
    text = json.dumps(data, ensure_ascii=False, indent=4) + "\n"
    with path.open("w", encoding=encoding, newline="\r\n") as handle:
        handle.write(text)


def chunk_items(items: Sequence[Tuple[str, str]], chunk_size: int) -> Iterable[List[Tuple[str, str]]]:
    for index in range(0, len(items), chunk_size):
        yield list(items[index : index + chunk_size])


def build_schema(keys: Sequence[str]) -> Dict[str, object]:
    properties = {key: {"type": "string"} for key in keys}
    return {
        "name": "resource_translation_batch",
        "strict": True,
        "schema": {
            "type": "object",
            "additionalProperties": False,
            "properties": properties,
            "required": list(keys),
        },
    }


def extract_message_content(payload: Dict[str, object]) -> str:
    try:
        return payload["choices"][0]["message"]["content"]
    except (KeyError, IndexError, TypeError) as exc:
        raise RuntimeError(f"Unexpected OpenAI response shape: {payload}") from exc


def call_openai_chat(
    *,
    api_key: str,
    model: str,
    locale: str,
    language_label: str,
    entries: Sequence[Tuple[str, str]],
) -> Dict[str, str]:
    keys = [key for key, _ in entries]
    prompt_lines = [
        f"Translate the following UI localization values from English to {language_label}.",
        "Return only translated string values for the provided keys.",
        "Keep the keys exactly the same.",
        "Preserve placeholders and formatting exactly, including but not limited to:",
        "- {0}, {1}, {Name}, {{escaped braces}}",
        "- %s, %1$s and similar printf-style tokens",
        "- Ellipses, punctuation, capitalization, line breaks, tabs, HTML, Markdown, and URLs",
        "- Product and protocol names such as Wino, Gmail, Outlook, IMAP, SMTP, OAuth, CalDav, Edge, Chrome, Firefox, Jodit, WebView2, SQLite",
        "Prefer natural UI phrasing for the target locale.",
        f"Locale code: {locale}",
        "",
        "Strings to translate:",
    ]
    for key, value in entries:
        prompt_lines.append(f"{key}: {value}")

    body = {
        "model": model,
        "messages": [
            {
                "role": "system",
                "content": (
                    "You are a software localization engine. "
                    "Translate accurately and conservatively. "
                    "Never omit keys. Never add commentary."
                ),
            },
            {"role": "user", "content": "\n".join(prompt_lines)},
        ],
        "response_format": {
            "type": "json_schema",
            "json_schema": build_schema(keys),
        },
    }

    request = urllib.request.Request(
        url="https://api.openai.com/v1/chat/completions",
        data=json.dumps(body).encode("utf-8"),
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=180) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        details = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"OpenAI API HTTP {exc.code}: {details}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"OpenAI API request failed: {exc}") from exc

    content = extract_message_content(payload)
    result = json.loads(content)
    if sorted(result.keys()) != sorted(keys):
        raise RuntimeError(
            "Translated batch returned the wrong key set. "
            f"Expected {len(keys)} keys, got {len(result)} keys."
        )
    return {key: result[key] for key in keys}


def translate_missing_entries(
    *,
    api_key: str,
    model: str,
    locale: str,
    entries: Sequence[Tuple[str, str]],
    chunk_size: int,
    max_retries: int,
) -> Dict[str, str]:
    if not entries:
        return {}

    translated: Dict[str, str] = {}
    language_label = LOCALE_LABELS.get(locale, locale)

    for chunk_index, chunk in enumerate(chunk_items(entries, chunk_size), start=1):
        last_error: Exception | None = None
        for attempt in range(1, max_retries + 1):
            try:
                result = call_openai_chat(
                    api_key=api_key,
                    model=model,
                    locale=locale,
                    language_label=language_label,
                    entries=chunk,
                )
                translated.update(result)
                print(
                    f"[{locale}] translated chunk {chunk_index} "
                    f"({len(chunk)} keys) on attempt {attempt}",
                    flush=True,
                )
                break
            except Exception as exc:  # noqa: BLE001
                last_error = exc
                if attempt == max_retries:
                    raise RuntimeError(
                        f"Failed translating chunk {chunk_index} for {locale}: {exc}"
                    ) from exc
                wait_seconds = attempt * 2
                print(
                    f"[{locale}] retrying chunk {chunk_index} after error: {exc}",
                    flush=True,
                )
                time.sleep(wait_seconds)

    return translated


def process_locale(
    locale: str,
    source_data: OrderedDict[str, str],
    translations_root: Path,
    args: argparse.Namespace,
    api_key: str | None,
) -> Dict[str, object]:
    path = translations_root / locale / "resources.json"
    if not path.exists():
        raise FileNotFoundError(f"Locale file not found: {path}")

    target_data = read_json(path)
    include_bom = has_utf8_bom(path)

    source_keys = list(source_data.keys())
    source_key_set = set(source_keys)
    target_key_set = set(target_data.keys())

    missing_keys = [key for key in source_keys if key not in target_key_set]
    extra_keys = [key for key in target_data.keys() if key not in source_key_set]

    translated_missing: Dict[str, str] = {}
    if missing_keys and args.apply:
        if not api_key:
            raise RuntimeError(
                f"Missing API key. Set the {args.api_key_env} environment variable before using --apply."
            )
        missing_entries = [(key, source_data[key]) for key in missing_keys]
        translated_missing = translate_missing_entries(
            api_key=api_key,
            model=args.model,
            locale=locale,
            entries=missing_entries,
            chunk_size=args.chunk_size,
            max_retries=args.max_retries,
        )

    merged = OrderedDict()
    for key in source_keys:
        if key in target_data:
            merged[key] = target_data[key]
        elif key in translated_missing:
            merged[key] = translated_missing[key]
        else:
            merged[key] = source_data[key]

    if args.apply:
        write_json(path, merged, include_bom=include_bom)

    return {
        "locale": locale,
        "path": str(path),
        "existing": len(source_keys) - len(missing_keys),
        "missing": len(missing_keys),
        "extra": len(extra_keys),
        "translated": len(translated_missing),
        "wrote": args.apply,
    }


def discover_locales(translations_root: Path, source_locale: str, requested: Sequence[str] | None) -> List[str]:
    if requested:
        return list(requested)
    locales = []
    for entry in sorted(translations_root.iterdir()):
        if entry.is_dir() and entry.name != source_locale and (entry / "resources.json").exists():
            locales.append(entry.name)
    return locales


def main() -> int:
    args = parse_args()
    translations_root = Path(args.translations_root)
    source_path = translations_root / args.source_locale / "resources.json"
    if not source_path.exists():
        print(f"Source file not found: {source_path}", file=sys.stderr)
        return 1

    source_data = read_json(source_path)
    locales = discover_locales(translations_root, args.source_locale, args.locales)
    if not locales:
        print("No target locales found.", file=sys.stderr)
        return 1

    api_key = os.environ.get(args.api_key_env)
    print(
        f"Processing {len(locales)} locale(s) from {source_path} "
        f"using model {args.model} in {'apply' if args.apply else 'dry-run'} mode.",
        flush=True,
    )

    results: List[Dict[str, object]] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=min(args.workers, len(locales))) as executor:
        future_map = {
            executor.submit(process_locale, locale, source_data, translations_root, args, api_key): locale
            for locale in locales
        }
        for future in concurrent.futures.as_completed(future_map):
            locale = future_map[future]
            try:
                result = future.result()
            except Exception as exc:  # noqa: BLE001
                print(f"[{locale}] failed: {exc}", file=sys.stderr, flush=True)
                return 1
            results.append(result)
            print(
                f"[{result['locale']}] missing={result['missing']} "
                f"extra={result['extra']} translated={result['translated']} "
                f"mode={'write' if result['wrote'] else 'preview'}",
                flush=True,
            )

    results.sort(key=lambda item: item["locale"])
    total_missing = sum(int(item["missing"]) for item in results)
    total_extra = sum(int(item["extra"]) for item in results)
    total_translated = sum(int(item["translated"]) for item in results)
    print(
        f"Done. locales={len(results)} missing={total_missing} "
        f"extra={total_extra} translated={total_translated}",
        flush=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
