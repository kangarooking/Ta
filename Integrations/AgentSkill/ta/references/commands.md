# CLI commands

Use `--json` in Agent workflows. Global options may appear anywhere:

```text
--json
--timeout <seconds>
--request-id <id>
--socket <path>
--cloud auto|allow|deny
```

Omit `--cloud`: the policy the user configured in Ta then applies. The flag can only make a request stricter (`--cloud deny`); it can never loosen the user's setting.

Default timeouts: 10 s for status/list commands, 30 s for capture/transform/copy/save, 180 s for `ocr`/`analyze`/`translate` (60 s for `ocr --cloud deny`). When the CLI stops waiting it returns `REQUEST_TIMEOUT` (exit code 8, `retryable: false`): Ta may still be running the request and may already have uploaded content, so do not retry automatically.

Output streams: success JSON goes to stdout; every failure — including launch failures and timeouts — goes to stderr, as the same JSON envelope when `--json` is set. Always read stderr when the exit code is non-zero.

## Inspect

```bash
ta status --json
ta capabilities --json
ta permissions --json
ta screen list --json
ta window list --json
ta window list --app "Safari" --json
```

`--app` accepts a visible app-name fragment or an exact Bundle ID. Use returned display and window IDs; do not guess them.

## Capture

```bash
ta capture frontmost --json
ta capture screen --json
ta capture screen --display 1 --json
ta capture window --window-id 1284 --json
ta capture app "Safari" --json
ta capture app "Safari" --title "Release notes" --json
ta capture window --title "Release notes" --json
ta capture region --display 1 --x 100 --y 120 --width 800 --height 600 --json
ta capture frontmost --max-size 1568 --json
ta capture frontmost --output /absolute/path/capture.png --json
```

Region coordinates are global macOS screen coordinates. `--output` saves a durable copy of exactly the artifact this command produced, while the response still describes the managed source artifact; `data.savedPath` is the path that was written.

`capture app <name-or-bundle-id>` resolves the window through the same list as `ta window list`: an exact app name or Bundle ID beats a name fragment, and the frontmost matching window wins. `--title` narrows by a case-insensitive title fragment. No match returns `TARGET_NOT_FOUND`.

`--max-size <pixels>` (16–16384) downscales the output so its longer side is at most that many pixels; smaller images are untouched. Use `--max-size 1568` for screenshots you will view yourself. `data.image` reports `width`, `height`, `originalWidth`, `originalHeight`, `downscaled`, and `pixelsPerPoint` (output pixels per screen point). After a downscaled capture, `last` and annotation coordinates refer to the downscaled image.

The command returns metadata, not pixels: open `artifacts[0].path` with an image-viewing tool to see the screenshot.

## Understand and translate

Commands without an image path use Ta's latest artifact:

```bash
ta ocr last --languages zh-Hans,en-US --json
ta ocr /absolute/path/image.png --json
ta ocr last --boxes --json
ta analyze last --task general --json
ta analyze last --task extractText --json
ta analyze last --task explainCode --json
ta analyze last --task tableMarkdown --json
ta analyze last --task formulaLaTeX --json
ta translate last --mode text --json
ta translate last --mode image --json
ta translate text --text "Hello" --json
```

For `translate --mode text`, the CLI runs OCR and then text translation. Image mode returns the translated text produced by the configured vision provider.

`ocr --boxes` adds `data.lines`: one entry per recognized line with `text`, `confidence`, and `box` (`x`, `y`, `width`, `height` in pixels of the recognized image, origin top-left — the same coordinate space annotation recipes use), plus `imageWidth`, `imageHeight`, and `boxesAvailable`. Cloud OCR engines return no line boxes (`boxesAvailable: false`).

Image inputs must be regular image files: png, jpg, jpeg, tiff, heic, webp, gif, or bmp. Anything else returns `INVALID_REQUEST`.

## Deliver

```bash
ta copy last --json
ta copy --text "content" --json
ta save last --output /absolute/path/result.png --json
ta save last --output /absolute/path/result.png --overwrite --json
```

Copy mutates the user's clipboard. Save requires an explicit absolute destination and has these rules (violations return `INVALID_REQUEST`):

- PNG only: a path without an extension gets `.png` appended; any other extension is rejected.
- No hidden files or directories (a path component starting with `.`), nothing under `~/Library`, and no system directories.
- The destination directory must already exist.
- An existing file is never replaced unless `--overwrite` is passed. Use it only when the user asked to replace that exact file; otherwise choose a new name.

## Annotate and transform

```bash
ta transform last --recipe /absolute/path/annotations.json --output /absolute/path/marked.png --json
ta transform /absolute/path/input.png --recipe /absolute/path/annotations.json --json
ta transform undo --output /absolute/path/previous.png --json
ta transform redo --output /absolute/path/restored.png --json
```

The CLI reads the local UTF-8 recipe and sends its contents—not the recipe path—to the Bridge. `last` uses the current Bridge image and edit session. An explicit input path starts a new edit session. Every successful command returns a new temporary PNG Artifact; `--output` additionally saves a durable copy.

Annotation transforms are local-only. Confirm `meta.cloudUploaded` is `false`, `artifacts` is non-empty, and `data.canUndo` / `data.canRedo` match the expected history state. See [editing-recipes.md](editing-recipes.md) for the versioned schema.

## Response contract

Success requires top-level `ok: true`. Image-producing success also requires at least one artifact with `path`, `mimeType`, `bytes`, `sha256`, and expiry metadata. `meta.cloudUploaded` is the authoritative disclosure for cloud usage. A successful transform also returns `width`, `height`, `elementCount`, `canUndo`, and `canRedo`.

Exit codes: `0` success, `2` usage, `3` app missing, `4` bridge unavailable, `5` permission denied, `6` privacy/cloud blocked, `7` request failure, `8` client timeout (`REQUEST_TIMEOUT`), `130` cancelled.
