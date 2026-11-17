# Privacy and safety

## Capture

After the user grants Ta macOS Screen Recording permission, ordinary silent captures do not require per-shot confirmation. This does not authorize capturing an obviously sensitive target the user did not identify.

Stop and ask before capturing password managers, banking/payment apps, recovery codes, private keys, authentication prompts, private browsing windows, or similarly sensitive content. Ta blocks common password managers (1Password, Bitwarden, Keychain Access, Passwords, LastPass, Dashlane, KeePassXC, Enpass, and others) by default, and a display capture is refused while one of them is visible. Respect `TARGET_BLOCKED_BY_PRIVACY_POLICY`; do not bypass the denylist with a display or region capture.

## Cloud

The cloud policy the user sets in Ta is an upper bound. Omit `--cloud` and it applies as-is. A request can only be stricter, never looser:

- `--cloud deny`: never upload for this request; a cloud-required operation fails clearly with `CLOUD_UPLOAD_NOT_ALLOWED`.
- `--cloud auto` / `--cloud allow`: no effect beyond the user's setting. If the user chose "always deny upload", these are still denied.

Always read `meta.cloudUploaded` rather than inferring cloud use from the command name. Ta stores provider secrets; neither this skill nor the CLI should request or expose them.

## Clipboard and files

`ta copy` changes the user's clipboard. Use it only when requested or when copying is the explicit deliverable. `ta save` writes a persistent file; use a user-approved destination. Ta only writes `.png` files, refuses hidden paths, `~/Library`, and system directories, and refuses to replace an existing file unless `--overwrite` is passed — use that flag only when the user asked to replace that exact file.

## Artifacts and logs

Managed artifacts are temporary local files with expiry and checksum metadata; Ta deletes them after 24 hours. Ta keeps a local audit record of each call (method, client, target app, saved path, whether content went to the cloud) but not the content. Do not paste image bytes into prompts or logs. Do not retain OCR text containing secrets. Save a durable copy only when the user asks to keep the result.

## Visible operations

Pinning, interactive selection, annotation UI, and native-app scrolling capture visibly affect the desktop. Silent capture authorization does not automatically authorize mouse/keyboard control. Never move the pointer or send synthetic input through Ta Skill.
