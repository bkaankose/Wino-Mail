# Sourced by the macOS release scripts before their architecture question.
# Expects REPO_ROOT, NON_INTERACTIVE, TRANSLATE (yes, no or empty), step() and fail().

set_release_translation() {
  [ -z "$TRANSLATE" ] || [ "$TRANSLATE" = "$1" ] || fail "Use either --translate or --skip-translation, not both."
  TRANSLATE="$1"
}

run_release_translation() {
  if [ -z "$TRANSLATE" ]; then
    if [ "$NON_INTERACTIVE" -eq 1 ]; then
      TRANSLATE="no"
    else
      printf '\nTranslate missing resources with translate_resources.py before building? [y/N]: '
      read -r answer
      case "${answer:-n}" in [Yy]*) TRANSLATE="yes" ;; *) TRANSLATE="no" ;; esac
    fi
  fi
  [ "$TRANSLATE" = "yes" ] || return 0

  step "Translations"
  [ -n "${WINO_OPENAI_API_KEY:-}" ] ||
    fail "WINO_OPENAI_API_KEY is not set. Set it as described in docs/local-script-environment.md, or pass --skip-translation."
  python3 "$REPO_ROOT/scripts/localization/translate_resources.py" --apply ||
    fail "Translation failed. Nothing was built."
}
