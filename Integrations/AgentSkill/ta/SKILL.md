---
name: ta
description: Take a screenshot on macOS with the Ta app and ta CLI — capture the screen, the frontmost app, a named app or window, or a known region without taking focus — then OCR, annotate, understand, translate, copy, or save the image. Use when the user says "take a screenshot", "screenshot this", "what's on my screen", "look at my screen", "截图", "截个图", "看一下屏幕", "看看我的屏幕", or when an Agent needs to see the screen, verify the UI visually, read on-screen text, or produce deterministic screenshot markup. Do not use it as a general mouse-and-keyboard automation tool.
---

# Ta Screen Intelligence

Use Ta as the visual input layer. Ta owns macOS Screen Recording permission, model profiles, and secrets; the CLI only sends requests to its local bridge.

## Start safely

1. Run `scripts/check-ta.sh` once before the first Ta action in a task.
2. Read the JSON response. Continue only when top-level `ok` is `true` and `data.bridge` is `ready`.
3. Use `--json` for every command an Agent must inspect. Treat a request as successful only when top-level `ok` is `true`; image-producing commands must also return a non-empty `artifacts` array.
4. Do not request, read, print, or pass API keys. Model configuration stays in Ta.

If the check fails, follow [references/error-codes.md](references/error-codes.md). Do not repeatedly launch Ta or retry non-retryable errors.

## Read results correctly

- Success JSON is printed on stdout. **Failures are printed on stderr** (as the same JSON envelope when `--json` is set) with a non-zero exit code. Capture both streams; an empty stdout means "read stderr", not "no result".
- A capture does not put the image in your context. The response only describes it: **open the file at `artifacts[0].path` with your image-viewing tool to actually see the screenshot.** Never describe a screenshot you have not opened.
- Add `--max-size 1568` to captures you intend to look at. Without it the PNG is full Retina resolution, which is large and slow to read. `data.image` reports the output `width`/`height`, the original size, and `pixelsPerPoint`.
- Timeouts: the CLI waits 10 s for status/list commands, 30 s for capture/transform/save, and 180 s for OCR/analyze/translate; override with `--timeout <seconds>`. `REQUEST_TIMEOUT` means the CLI stopped waiting while Ta may still be working — and may already have uploaded to a cloud model. **Never retry it automatically.**

## Choose the workflow

- Capture the current page or app: `ta capture frontmost --json`. Ta freezes the foreground target and should not activate itself, move the pointer, or send input events.
- Capture a named app in one call: `ta capture app "Safari" --max-size 1568 --json` (add `--title "<fragment>"` to pick a window). Verify the returned `data.target`.
- Capture a particular visible window when several are plausible: list candidates first, then capture the returned ID. Read [references/capture-targeting.md](references/capture-targeting.md).
- Extract text: capture, then run local OCR. Add `--boxes` when you need where each line is (for example to annotate it). Read [references/image-understanding.md](references/image-understanding.md).
- Understand semantics, code, tables, or formulas: use `ta analyze` only when OCR is insufficient. This may use the configured cloud model. Do not pass `--cloud`; the user's setting in Ta decides.
- Translate visible content: choose text translation for a textual result or image translation for a rendered translated image. Read [references/image-understanding.md](references/image-understanding.md).
- Copy or save a result: do so only when the request calls for changing the clipboard or filesystem. Read [references/commands.md](references/commands.md).
- Annotate an image reproducibly: use `ta transform ... --recipe <JSON>` and keep stable IDs for later erasing. The transform is local and returns a new PNG Artifact. Read [references/editing-recipes.md](references/editing-recipes.md).
- Beautify, redact, pin, or long-capture: check `ta capabilities --json` first. Do not invent methods absent from the returned list.

## Privacy boundary

Ordinary screenshot requests are pre-authorized after the user grants Ta macOS permission. Still stop before capturing a password manager, banking/payment app, private-key or recovery-code screen, private browsing window, or another obviously sensitive target unless the user explicitly identifies that target for capture.

Use local OCR when text alone is enough. Omit `--cloud` so the policy the user chose in Ta applies; pass `--cloud deny` only to be stricter. The flag can never loosen the user's setting, so do not try `--cloud allow` after `CLOUD_UPLOAD_NOT_ALLOWED`. Do not silently switch a cloud-denied task to cloud processing or an unavailable silent long capture to an interactive flow. Read [references/privacy-and-safety.md](references/privacy-and-safety.md) for cloud, clipboard, and artifact rules.

## Report observable results

Report the target actually returned by Ta, whether cloud upload occurred (`meta.cloudUploaded`, which is also true when an upload happened and the call then failed), and the resulting text or artifact path. Temporary artifacts are deleted after 24 hours; save a durable copy when the user needs the image later. Never claim success from command dispatch alone.
