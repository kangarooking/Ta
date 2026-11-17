# Capture targeting

## Current foreground content

Interpret “current page”, “what I am looking at”, or “this app” as the foreground window at request time:

```bash
ta capture frontmost --json
```

Do not open Ta, activate a different app, or list windows first unless disambiguation is required; those actions can change the meaning of “current”. Verify the returned `data.target.appName`, `bundleIdentifier`, title, and frame.

## Named app or exact window

When the user names an app and any of its windows will do, capture it in one call:

```bash
ta capture app "Safari" --max-size 1568 --json
ta capture app "Safari" --title "Release notes" --json
```

Ta picks the frontmost window whose app name or Bundle ID matches (an exact match beats a fragment); `--title` narrows by a title fragment. Always verify `data.target` in the response, and fall back to the list-then-capture flow below if it is not the window the user meant.

When there are multiple plausible windows:

1. Run `ta window list --app "<name-or-bundle-id>" --json`.
2. Compare app name, Bundle ID, title, frame, and z-order.
3. If one candidate unambiguously matches, call `ta capture window --window-id <id> --json`.
4. If several candidates remain plausible and their titles do not resolve the request, ask the user instead of guessing.

Window IDs are ephemeral. Re-list after `TARGET_NOT_FOUND` or `TARGET_CHANGED`; retry the capture at most once with the refreshed ID.

## Seeing the result

Capture commands return a description, not the pixels. Open `artifacts[0].path` with your image-viewing tool before describing or reasoning about the screenshot. Prefer `--max-size 1568` for images you will view; the default output is full Retina resolution. `data.image.pixelsPerPoint` converts between image pixels and the screen-point frames reported in `data.target.frame`.

## Display or known region

List displays before referring to a non-default display. Use region capture only when coordinates are known from the task or a preceding result. Do not introduce an interactive selection overlay merely to discover coordinates.

## Silent-mode invariant

The ordinary capture methods are expected to:

- leave the foreground application unchanged;
- leave pointer position and keyboard focus unchanged;
- omit the cursor;
- avoid Ta windows and selection overlays.

If an operation requires visible UI—interactive selection, pinning, annotation, or a native-app scrolling capture—describe the impact and obtain the authorization appropriate to that visible action. Never simulate mouse or keyboard events through this skill.
