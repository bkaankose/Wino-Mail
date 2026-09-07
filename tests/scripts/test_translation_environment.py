"""Offline tests for translation settings and credential handling."""

import contextlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
import urllib.error
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import translate_resources as translate
import validate_resources as validate


class TranslationEnvironmentTests(unittest.TestCase):
    def test_models_use_wino_settings_and_cli_override(self):
        for module, key in (
            (translate, "WINO_TRANSLATION_MODEL"),
            (validate, "WINO_TRANSLATION_VALIDATION_MODEL"),
        ):
            with self.subTest(module=module.__name__), mock.patch.dict(os.environ, {key: "fixture-model"}, clear=True):
                with mock.patch.object(sys, "argv", ["script", "--dry-run"]):
                    self.assertEqual(module.parse_args().model, "fixture-model")
                with mock.patch.object(sys, "argv", ["script", "--dry-run", "--model", "override"]):
                    self.assertEqual(module.parse_args().model, "override")

    def test_defaults_resolve_repository_files(self):
        with mock.patch.object(sys, "argv", ["script", "--dry-run"]):
            args = validate.parse_args()
        self.assertTrue((Path(args.translations_root) / "en_US" / "resources.json").is_file())
        self.assertEqual(Path(args.allowlist).parent.name, ".config")
        self.assertIn("Gmail", validate.read_allowlist(Path(args.allowlist))["values"])

    def run_fixture(self, module, mode, environment):
        with tempfile.TemporaryDirectory(prefix="wino-translation-test-") as directory:
            root = Path(directory)
            for locale, values in (
                ("en_US", {"TestMessage": "Please open your account settings", "MissingMessage": "Add a new account"}),
                ("de_DE", {"TestMessage": "Please open your account settings"}),
            ):
                (root / locale).mkdir()
                (root / locale / "resources.json").write_text(json.dumps(values), encoding="utf-8")
            target = root / "de_DE" / "resources.json"
            original = target.read_bytes()
            output = io.StringIO()
            arguments = ["script", mode, "--translations-root", directory, "--workers", "1"]
            with mock.patch.dict(os.environ, environment, clear=True), mock.patch.object(sys, "argv", arguments), \
                    mock.patch.object(module, "translate_missing_entries", return_value={"MissingMessage": "Neues Konto", "TestMessage": "Kontoeinstellungen"}) as api, \
                    contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                result = module.main()
            return result, output.getvalue(), api.call_args, original == target.read_bytes()

    def test_dry_runs_need_no_key_and_do_not_write(self):
        for module in (translate, validate):
            with self.subTest(module=module.__name__):
                result, _, call, unchanged = self.run_fixture(module, "--dry-run", {})
                self.assertEqual(result, 0)
                self.assertIsNone(call)
                self.assertTrue(unchanged)

    def test_legacy_key_is_not_a_fallback(self):
        for module in (translate, validate):
            with self.subTest(module=module.__name__):
                result, output, call, unchanged = self.run_fixture(module, "--apply", {"OPENAI_API_KEY": "legacy-fixture"})
                self.assertEqual(result, 1)
                self.assertIn("WINO_OPENAI_API_KEY", output)
                self.assertNotIn("legacy-fixture", output)
                self.assertIsNone(call)
                self.assertTrue(unchanged)

    def test_apply_passes_only_the_wino_key_to_translation(self):
        for module in (translate, validate):
            with self.subTest(module=module.__name__):
                result, output, call, _ = self.run_fixture(module, "--apply", {"WINO_OPENAI_API_KEY": "wino-fixture"})
                self.assertEqual(result, 0)
                self.assertEqual(call.kwargs["api_key"], "wino-fixture")
                self.assertNotIn("wino-fixture", output)

    def test_http_error_redacts_the_api_key(self):
        failure = urllib.error.HTTPError("https://example.invalid", 401, "unauthorized", {}, io.BytesIO(b"Invalid key fixture-secret"))
        with mock.patch.object(translate.urllib.request, "urlopen", side_effect=failure):
            with self.assertRaises(RuntimeError) as error:
                translate.call_openai_chat(api_key="fixture-secret", model="fixture", locale="de_DE", language_label="German", entries=[("A", "Open settings")])
        self.assertNotIn("fixture-secret", str(error.exception))
        self.assertIn("[redacted]", str(error.exception))


if __name__ == "__main__":
    unittest.main()
