---
name: ta
description: Use the Ta Windows desktop app and ta CLI as a consent-gated visual input layer. It can report Bridge status, capture the current display only after the user enables Agent Bridge, and run local OCR on the returned screenshot artifact. Do not use it for mouse/keyboard automation, cloud uploads, or unattended scrolling.
---

# Ta Windows Screen Intelligence

Ta owns Windows screen capture, local OCR models, model settings, and Agent authorization. The CLI talks only to Ta's local named-pipe Bridge, whose ACL permits the current Windows user only.

## Start safely

1. Run `scripts/check-ta.ps1` before the first Ta action in a task.
2. Read its JSON. Continue only when top-level `ok` is `true` and `data.bridge` is `ready`.
3. If a screen action fails with `AGENT_DISABLED`, tell the user to enable Bridge in **Ta → 设置 → Agent**. Do not attempt to alter that setting on the user's behalf.
4. Use `--json` and check the top-level `ok`; screenshot commands additionally require a non-empty `artifacts` array.

## Available Windows Bridge v1 actions

- `ta status --json` — non-sensitive Bridge health check.
- `ta capabilities --json` — read the actual advertised method list before choosing an action.
- `ta permissions --json` — confirm whether local OCR and screen capture are enabled.
- `ta capture screen --json` — captures the display at the pointer. It is available only after the user explicitly enables Agent Bridge from the same Windows user account. It returns a temporary PNG artifact.
- `ta ocr --artifact <ID> --json` — local OCR for the artifact returned by the immediately preceding capture; it does not upload the image.

## Consent and privacy boundary

- Do not enable Agent Bridge or alter its policy on the user's behalf. Those are user-facing authorization actions.
- Do not capture password managers, banking/payment pages, recovery codes, private browsing, or obviously sensitive personal data without the user's specific request.
- Do not infer support for scrolling capture, window targeting, clipboard writes, saving, pinning, model calls, or cloud recognition. They are intentionally absent from Windows Bridge v1; always trust `ta capabilities --json`.
- The Bridge audit log contains only timestamp, caller name, method, result, and cloud-upload flag. Never place image bytes, OCR text, or API keys in audit output.

## Report observable results

State which display Ta captured, whether local OCR was used, and the returned artifact path or text. Do not claim success merely because a CLI command was dispatched.
