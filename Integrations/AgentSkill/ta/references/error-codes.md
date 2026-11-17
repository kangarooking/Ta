# Error handling

Read the structured `error.code`, `message`, `hint`, and `retryable` fields. Do not parse localized prose.

Failures are written to **stderr** with a non-zero exit code. With `--json` they use the same envelope as successes (`{"ok":false,"error":{...}}`), including errors raised by the CLI itself before Ta answered. If stdout is empty, read stderr.

## Codes emitted by Ta (the Bridge)

| Code | Action |
|---|---|
| `PROTOCOL_VERSION_MISMATCH` | Stop and request compatible App/CLI versions. |
| `SCREEN_PERMISSION_REQUIRED` | Stop and direct the user to Ta Settings → Permissions. Do not retry until permission changes. |
| `INVALID_REQUEST` | The parameters were rejected: unknown option or task, missing argument, invalid or oversized annotation recipe, unreadable or non-image input path, an expired artifact path, or a refused save destination (not `.png`, hidden path, `~/Library`, system directory, missing directory, or existing file without `--overwrite`). Fix the request using `message`; do not resend it unchanged. |
| `TARGET_NOT_FOUND` | Re-list windows/displays and retry once only when the target is still unambiguous. |
| `TARGET_CHANGED` | Refresh the target and retry once. This is the only Bridge error marked `retryable: true`. |
| `TARGET_BLOCKED_BY_PRIVACY_POLICY` | Stop. Agent access is off, automatic capture is off, or the target (or an app visible in a display capture) is on the user's privacy denylist — password managers are blocked by default. Do not switch to a display or region capture to evade the policy. |
| `MODEL_PROFILE_NOT_CONFIGURED` | Stop and ask the user to configure the model in Ta. Never ask for the API key in chat. |
| `CLOUD_UPLOAD_NOT_ALLOWED` | Stop or fall back to a genuinely local operation such as local OCR. The user's setting in Ta is an upper bound: `--cloud allow` cannot override it, so do not retry with a different `--cloud` value. |
| `CANCELLED` | Treat as cancelled; do not automatically restart. |
| `INTERNAL_ERROR` | Ta or the configured model provider failed (for example a provider/network error during analyze or translate, or an encoding failure). Report `message` to the user. Check `meta.cloudUploaded`: it is `true` if content was already sent before the failure. Do not loop. |

## Codes produced on the client side

| Code | Source | Action |
|---|---|---|
| `TA_CLI_NOT_INSTALLED` | `scripts/check-ta.sh` | The `ta` executable is not on `PATH` or in `~/.local/bin`. Ask the user to install the CLI from Ta Settings → Agent. Do not look for the app instead. |
| `TA_APP_NOT_INSTALLED` | `ta` CLI | The Ta app was not found. Stop and explain that Ta must be installed (or `TA_APP_PATH` set); do not loop. |
| `BRIDGE_UNAVAILABLE` | `ta` CLI | The CLI could not reach Ta, after already attempting one non-activating launch. When `retryable` is `true` (connection could not be established) retry at most once. When `retryable` is `false` the connection broke after the request was sent: the request may have run, so check the result before doing anything else. |
| `REQUEST_TIMEOUT` | `ta` CLI | The CLI stopped waiting (exit code 8), but **the request may still be running in Ta and content may already have been uploaded to a cloud model**. Never retry automatically — that can upload twice. Tell the user; if they want to wait longer, re-run once with a larger `--timeout`. |
| `INVALID_REQUEST` | `ta` CLI | With `--json`, a usage error (unknown command or flag, exit code 2) is reported with this code. Fix the command line. |
| `CANCELLED` | `ta` CLI | The CLI was interrupted (exit code 130). Do not restart automatically. |

There is no dedicated "artifact expired" code: temporary artifacts are deleted after 24 hours, and using a deleted path returns `INVALID_REQUEST`. Re-run the capture if it is still authorized.

## Retry rules

- Retry only when `retryable` is `true`, and at most once.
- Never retry `REQUEST_TIMEOUT`, `CLOUD_UPLOAD_NOT_ALLOWED`, `TARGET_BLOCKED_BY_PRIVACY_POLICY`, `SCREEN_PERMISSION_REQUIRED`, or `MODEL_PROFILE_NOT_CONFIGURED` on your own.

Expected fixture behavior:

- No `ta` CLI: `TA_CLI_NOT_INSTALLED`, installation guidance, no further commands.
- No Ta app: installation guidance, no repeated launch.
- Permission missing: permission guidance, no retry.
- Healthy bridge: proceed and validate `ok` plus artifacts/data.
- Cloud denied: stay local or report that the requested model operation is unavailable.
- Slow cloud call that times out: report that it may still be running; no automatic retry.
