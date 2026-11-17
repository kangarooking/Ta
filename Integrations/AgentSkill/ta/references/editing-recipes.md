# Deterministic annotation recipes

Check `ta capabilities --json` and continue only when `transform.image` is present. Annotation transforms are non-interactive, local-only operations. They do not open the GUI editor, move the pointer, change keyboard focus, or upload the image.

## Commands

```bash
ta transform last --recipe /absolute/path/annotations.json --output /absolute/path/marked.png --json
ta transform /absolute/path/input.png --recipe /absolute/path/annotations.json --json
ta transform undo --output /absolute/path/previous.png --json
ta transform redo --output /absolute/path/restored.png --json
```

Use an explicit image path to start a new vector editing session. `last` continues the current session or starts from the latest capture. A normal Ta capture also starts a new image and clears the previous annotation session. App restart clears history.

Each successful `transform` request is one atomic history step. One recipe may contain many operations; if any operation is invalid, none of them are applied. History retains up to 100 steps. A new transform after undo clears the redo stack.

## Recipe v1

Coordinates use image pixels with the origin at the top-left. A crop may appear once and must be the first operation. Operations after crop use the cropped canvas coordinates.

```json
{
  "version": 1,
  "operations": [
    {
      "type": "crop",
      "rect": {"x": 0, "y": 0, "width": 900, "height": 600}
    },
    {
      "type": "rectangle",
      "id": "primary-action",
      "rect": {"x": 48, "y": 72, "width": 300, "height": 120},
      "color": "#FF3B30",
      "lineWidth": 6,
      "dashed": false
    },
    {
      "type": "arrow",
      "id": "primary-arrow",
      "start": {"x": 520, "y": 120},
      "end": {"x": 350, "y": 130},
      "color": "#FF3B30",
      "lineWidth": 6
    },
    {
      "type": "text",
      "id": "primary-label",
      "origin": {"x": 530, "y": 88},
      "text": "从这里开始拓取",
      "color": "#FF3B30",
      "fontSize": 28
    }
  ]
}
```

Colors accept `#RRGGBB` or `#RRGGBBAA`. All visible operations require a unique, non-empty `id`.

## Supported operations

- `crop`: `rect`.
- `rectangle`, `ellipse`: `id`, `rect`, optional `color`, `lineWidth`, and `dashed`.
- `arrow`: `id`, `start`, `end`, optional `color`, `lineWidth`, and `dashed`.
- `pen`, `highlighter`: `id`, non-empty `points`, optional `color`, `lineWidth`, and `dashed`. Highlighter defaults to translucent yellow.
- `text`: `id`, `origin`, non-empty `text`, optional `color` and `fontSize`.
- `number`: `id`, `center`, integer `number`, optional `color` and `diameter`.
- `mosaic` rectangle: `id`, `mode: "rect"`, `rect`, optional `scale`.
- `mosaic` brush: `id`, `mode: "brush"`, non-empty `points`, optional `lineWidth` and `scale`.
- `blur`: `id`, `rect`, optional positive `radius`.
- `magnify`: `id`, `rect`, optional `factor` greater than 1.
- `eraser`: non-empty `targetIds`. IDs may refer to the current recipe or the existing session.

Defaults: red `#FF3B30`, line width `5`, text size `24`, number diameter `28`, mosaic brush width `24`, mosaic scale `14`, blur radius `12`, and magnification factor `2`.

## Erase and history example

```json
{
  "version": 1,
  "operations": [
    {"type": "eraser", "targetIds": ["primary-arrow", "primary-label"]}
  ]
}
```

Run it against `last`, then use `ta transform undo --json` to restore both objects. Erasing works on vector IDs; it does not guess a target from simulated mouse coordinates.

## Validation and privacy

Invalid versions, duplicate IDs, missing erase targets, empty paths/text, non-finite coordinates, non-positive sizes, repeated/late crops, and out-of-bounds crops fail with `INVALID_REQUEST`. So do values above the upper bounds: coordinates and rect sizes up to ±1,000,000; `lineWidth` and `fontSize` up to 2,000; `diameter` up to 4,000; mosaic `scale` up to 512; blur `radius` up to 256; magnify `factor` up to 64; at most 500 operations per recipe, 20,000 points per path, and 4,000 characters per text. Do not silently clamp a malformed recipe and do not replace a failed deterministic transform with GUI automation.

The renderer uses local Core Graphics, Core Text, and Accelerate operations. A successful response must report `meta.cloudUploaded: false` and include a PNG Artifact. Save to a new explicit path when the user needs a durable result.

Beautification, redaction, pinning, interactive editing, and scrolling capture remain separate capabilities. Only use them when `ta capabilities --json` advertises the corresponding method.
