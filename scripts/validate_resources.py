#!/usr/bin/env python3
"""
Audit translated Wino `resources.json` files for values that still look English
and optionally retranslate only those suspect values via the OpenAI API.

By default this script runs in dry-run mode and only reports suspect keys.
Use `--apply` to write translated replacements.

Examples:
  python scripts/validate_resources.py --dry-run
  python scripts/validate_resources.py --dry-run --locales de_DE --report scripts/translation_audit.json
  python scripts/validate_resources.py --apply --locales da_DK --model gpt-5-nano
  python scripts/validate_resources.py --dry-run --suspect-mode heuristic

Usage:
    $env:OPENAI_API_KEY="{open ai key here}"
    python .\\scripts\\validate_resources.py --dry-run
    python .\\scripts\\validate_resources.py --apply --locales da_DK --workers 2
"""

from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
import re
import sys
from collections import OrderedDict
from pathlib import Path
from typing import Dict, List, Sequence

from translate_resources import (
    LOCALE_LABELS,
    discover_locales,
    has_utf8_bom,
    read_json,
    translate_missing_entries,
    write_json,
)


NON_LATIN_LOCALES = {
    "bg_BG",
    "el_GR",
    "ja_JP",
    "ko_KR",
    "ru_RU",
    "uk_UA",
    "zh_CN",
}

ENGLISH_HINT_WORDS = {
    "about",
    "account",
    "add",
    "address",
    "all",
    "allow",
    "and",
    "are",
    "authentication",
    "background",
    "browser",
    "button",
    "calendar",
    "can",
    "change",
    "click",
    "clipboard",
    "complete",
    "computer",
    "configuration",
    "continue",
    "copy",
    "create",
    "delete",
    "details",
    "display",
    "download",
    "edit",
    "email",
    "enable",
    "enter",
    "failed",
    "file",
    "folder",
    "from",
    "has",
    "have",
    "if",
    "image",
    "launch",
    "mail",
    "message",
    "new",
    "not",
    "notification",
    "open",
    "or",
    "password",
    "please",
    "profile",
    "reset",
    "save",
    "search",
    "select",
    "send",
    "settings",
    "sign",
    "synchronization",
    "synchronize",
    "the",
    "this",
    "to",
    "use",
    "warning",
    "will",
    "with",
    "you",
    "your",
}

EMAIL_RE = re.compile(r"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b")
FORMAT_TOKEN_RE = re.compile(r"(\{\{|\}\}|\{\d+\}|\{[A-Za-z_][A-Za-z0-9_]*\}|%(\d+\$)?[sdif])")
URL_RE = re.compile(r"\b(?:https?://|www\.)\S+\b", re.IGNORECASE)
WORD_RE = re.compile(r"[A-Za-z][A-Za-z']*")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Audit and optionally repair untranslated Wino resources.json values."
    )
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
        help="OpenAI model name to use when --apply is enabled.",
    )
    parser.add_argument(
        "--chunk-size",
        type=int,
        default=100,
        help="How many suspect keys to translate per API request.",
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
        "--suspect-mode",
        choices=("exact", "heuristic"),
        default="exact",
        help="Use exact English matches only, or also flag conservative English-looking text.",
    )
    parser.add_argument(
        "--allowlist",
        default=str(Path("scripts") / "translation_allowlist.json"),
        help="Path to JSON allowlist for legitimate untranslated keys or values.",
    )
    parser.add_argument(
        "--report",
        help="Optional path for a JSON audit report.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Report suspect values without writing files or calling the OpenAI API.",
    )
    parser.add_argument(
        "--apply",
        action="store_true",
        help="Translate suspect values and write the updated files back to disk.",
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


def read_allowlist(path: Path) -> Dict[str, object]:
    if not path.exists():
        return {
            "keys": set(),
            "values": set(),
            "key_values": {},
            "key_patterns": [],
            "value_patterns": [],
        }

    with path.open("r", encoding="utf-8-sig") as handle:
        data = json.load(handle)

    return {
        "keys": set(data.get("keys", [])),
        "values": set(data.get("values", [])),
        "key_values": data.get("key_values", {}),
        "key_patterns": [re.compile(pattern) for pattern in data.get("key_patterns", [])],
        "value_patterns": [re.compile(pattern) for pattern in data.get("value_patterns", [])],
    }


def has_letters(value: str) -> bool:
    return any(character.isalpha() for character in value)


def is_format_only(value: str) -> bool:
    without_tokens = FORMAT_TOKEN_RE.sub("", value)
    return not has_letters(without_tokens)


def is_email_or_url_only(value: str) -> bool:
    stripped = value.strip()
    without_urls = URL_RE.sub("", stripped)
    without_emails = EMAIL_RE.sub("", without_urls)
    return bool(stripped) and not has_letters(without_emails)


def is_acronym_like(value: str) -> bool:
    stripped = value.strip()
    if not stripped or len(stripped) > 24:
        return False
    if not has_letters(stripped):
        return True

    letters = [character for character in stripped if character.isalpha()]
    if not letters:
        return True
    uppercase_letters = [character for character in letters if character.upper() == character]
    return len(uppercase_letters) == len(letters)


def ascii_letter_ratio(value: str) -> float:
    letters = [character for character in value if character.isalpha()]
    if not letters:
        return 0
    ascii_letters = [character for character in letters if character.isascii()]
    return len(ascii_letters) / len(letters)


def get_words(value: str) -> List[str]:
    without_urls = URL_RE.sub(" ", value)
    without_emails = EMAIL_RE.sub(" ", without_urls)
    without_tokens = FORMAT_TOKEN_RE.sub(" ", without_emails)
    return [match.group(0).lower().strip("'") for match in WORD_RE.finditer(without_tokens)]


def is_sentence_like_english(value: str) -> bool:
    words = get_words(value)
    if len(words) < 3:
        return False

    hint_count = sum(1 for word in words if word in ENGLISH_HINT_WORDS)
    if hint_count >= 2:
        return True

    return len(words) >= 5 and hint_count >= 1 and ascii_letter_ratio(value) >= 0.9


def is_allowlisted(
    *,
    key: str,
    value: str,
    allowlist: Dict[str, object],
) -> bool:
    keys = allowlist["keys"]
    values = allowlist["values"]
    key_values = allowlist["key_values"]
    key_patterns = allowlist["key_patterns"]
    value_patterns = allowlist["value_patterns"]

    if key in keys or value in values:
        return True
    if key_values.get(key) == value:
        return True
    if any(pattern.search(key) for pattern in key_patterns):
        return True
    if any(pattern.search(value) for pattern in value_patterns):
        return True
    if is_format_only(value) or is_email_or_url_only(value) or is_acronym_like(value):
        return True

    return False


def get_suspect_reason(
    *,
    locale: str,
    key: str,
    source_value: str,
    target_value: str,
    suspect_mode: str,
    allowlist: Dict[str, object],
) -> str | None:
    if is_allowlisted(key=key, value=target_value, allowlist=allowlist):
        return None

    if target_value == source_value:
        return "exact_english"

    if suspect_mode != "heuristic":
        return None

    if locale in NON_LATIN_LOCALES:
        if ascii_letter_ratio(target_value) >= 0.85 and is_sentence_like_english(target_value):
            return "non_latin_ascii_english"
        return None

    if ascii_letter_ratio(target_value) >= 0.9 and is_sentence_like_english(target_value):
        return "english_looking"

    return None


def find_suspects(
    *,
    locale: str,
    source_data: OrderedDict[str, str],
    target_data: OrderedDict[str, str],
    suspect_mode: str,
    allowlist: Dict[str, object],
) -> List[Dict[str, str]]:
    suspects = []
    for key, source_value in source_data.items():
        if key not in target_data:
            continue

        target_value = target_data[key]
        reason = get_suspect_reason(
            locale=locale,
            key=key,
            source_value=source_value,
            target_value=target_value,
            suspect_mode=suspect_mode,
            allowlist=allowlist,
        )
        if reason:
            suspects.append(
                {
                    "key": key,
                    "reason": reason,
                    "source": source_value,
                    "target": target_value,
                }
            )

    return suspects


def process_locale(
    locale: str,
    source_data: OrderedDict[str, str],
    translations_root: Path,
    args: argparse.Namespace,
    allowlist: Dict[str, object],
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
    exact_english_count = sum(
        1 for key in source_keys if key in target_data and target_data[key] == source_data[key]
    )

    suspects = find_suspects(
        locale=locale,
        source_data=source_data,
        target_data=target_data,
        suspect_mode=args.suspect_mode,
        allowlist=allowlist,
    )
    suspect_reasons: Dict[str, int] = {}
    for suspect in suspects:
        reason = suspect["reason"]
        suspect_reasons[reason] = suspect_reasons.get(reason, 0) + 1

    translated: Dict[str, str] = {}
    if suspects and args.apply:
        if not api_key:
            raise RuntimeError(
                f"Missing API key. Set the {args.api_key_env} environment variable before using --apply."
            )
        suspect_entries = [(suspect["key"], source_data[suspect["key"]]) for suspect in suspects]
        translated = translate_missing_entries(
            api_key=api_key,
            model=args.model,
            locale=locale,
            entries=suspect_entries,
            chunk_size=args.chunk_size,
            max_retries=args.max_retries,
        )

        merged = OrderedDict()
        for key, value in target_data.items():
            merged[key] = translated.get(key, value)
        write_json(path, merged, include_bom=include_bom)

    return {
        "locale": locale,
        "path": str(path),
        "missing": len(missing_keys),
        "extra": len(extra_keys),
        "exact_english": exact_english_count,
        "suspect": len(suspects),
        "translated": len(translated),
        "wrote": bool(translated),
        "suspect_reasons": suspect_reasons,
        "suspects": suspects,
    }


def write_report(path: Path, results: Sequence[Dict[str, object]], args: argparse.Namespace) -> None:
    suspect_reasons: Dict[str, int] = {}
    for result in results:
        for reason, count in result["suspect_reasons"].items():
            suspect_reasons[reason] = suspect_reasons.get(reason, 0) + int(count)

    report = {
        "source_locale": args.source_locale,
        "suspect_mode": args.suspect_mode,
        "locales": list(results),
        "totals": {
            "locales": len(results),
            "missing": sum(int(item["missing"]) for item in results),
            "extra": sum(int(item["extra"]) for item in results),
            "exact_english": sum(int(item["exact_english"]) for item in results),
            "suspect": sum(int(item["suspect"]) for item in results),
            "suspect_reasons": suspect_reasons,
            "translated": sum(int(item["translated"]) for item in results),
        },
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


def main() -> int:
    args = parse_args()
    translations_root = Path(args.translations_root)
    source_path = translations_root / args.source_locale / "resources.json"
    if not source_path.exists():
        print(f"Source file not found: {source_path}", file=sys.stderr)
        return 1

    allowlist_path = Path(args.allowlist)
    source_data = read_json(source_path)
    allowlist = read_allowlist(allowlist_path)
    locales = discover_locales(translations_root, args.source_locale, args.locales)
    if not locales:
        print("No target locales found.", file=sys.stderr)
        return 1

    api_key = os.environ.get(args.api_key_env)
    print(
        f"Auditing {len(locales)} locale(s) from {source_path} "
        f"using suspect-mode={args.suspect_mode} in {'apply' if args.apply else 'dry-run'} mode.",
        flush=True,
    )
    print(f"Allowlist: {allowlist_path}", flush=True)

    results: List[Dict[str, object]] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=min(args.workers, len(locales))) as executor:
        future_map = {
            executor.submit(
                process_locale,
                locale,
                source_data,
                translations_root,
                args,
                allowlist,
                api_key,
            ): locale
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
                f"extra={result['extra']} exact_english={result['exact_english']} "
                f"suspect={result['suspect']} translated={result['translated']} "
                f"mode={'write' if result['wrote'] else 'preview'}",
                flush=True,
            )

    results.sort(key=lambda item: item["locale"])
    total_missing = sum(int(item["missing"]) for item in results)
    total_extra = sum(int(item["extra"]) for item in results)
    total_exact_english = sum(int(item["exact_english"]) for item in results)
    total_suspect = sum(int(item["suspect"]) for item in results)
    total_translated = sum(int(item["translated"]) for item in results)

    if args.report:
        report_path = Path(args.report)
        write_report(report_path, results, args)
        print(f"Wrote audit report: {report_path}", flush=True)

    print(
        f"Done. locales={len(results)} missing={total_missing} extra={total_extra} "
        f"exact_english={total_exact_english} suspect={total_suspect} "
        f"translated={total_translated}",
        flush=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
