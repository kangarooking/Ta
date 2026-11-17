# Image understanding and translation

## Prefer the least invasive route

1. Text only: use `ta ocr`. This uses the OCR engine configured in Ta and can remain local.
2. Semantic interpretation or structure: use `ta analyze` with the narrowest matching task.
3. Translation: OCR plus text translation for copyable text; image translation only when layout-preserving visual output is requested.

Do not send the image to a cloud model merely to recover ordinary readable text. Leave `--cloud` out so the policy the user set in Ta applies. If the task itself forbids cloud use, pass `--cloud deny`. The flag can only be stricter than the user's setting, so never retry with `allow` after `CLOUD_UPLOAD_NOT_ALLOWED`.

`analyze` and `translate` can take a minute or more; the CLI waits up to 180 s by default. If it returns `REQUEST_TIMEOUT`, the request may still be running and the image may already have been uploaded — do not send it again automatically.

## Reliable chaining

Capture and immediately consume the returned artifact, or use `last` only within the same clear workflow. In concurrent work, prefer the explicit artifact path so another request cannot replace “last”.

Examples:

```bash
ta capture frontmost --json
ta ocr last --languages zh-Hans,en-US --json
```

```bash
ta analyze /absolute/path/capture.png --task tableMarkdown --json
```

```bash
ta translate /absolute/path/capture.png --mode text --json
```

```bash
ta ocr /absolute/path/capture.png --boxes --json
```

With `--boxes`, `data.lines[].box` gives each line's pixel rectangle (top-left origin). Those numbers can be used directly as `rect` values in an annotation recipe for the same image.

## Validate output

- OCR: require `ok: true`; report text, engine, languages, content type, and confidence when present.
- Analyze/translate: require non-empty `data.text`; disclose `meta.cloudUploaded`. It is `true` whenever content was handed to a cloud model, including when the call failed afterwards.
- An empty OCR result is not a successful extraction. Consider a configured enhanced OCR engine or semantic analysis only if the user's privacy constraints allow it.
