# dsh-ta

Native DeepSeek Harness Cordis tools for the Ta macOS screenshot app. The bundle talks to Ta's local Unix-domain Bridge; it never reads model API keys and imports image results into Harness durable attachments.

Requires DeepSeek Harness `0.1.0-rc.7` or newer. Cordis, Tool Runtime, and Attachment Store are supplied by the Harness installation, while this package keeps only its schema dependency in the distributable bundle.

```bash
dsh plugin --profile web add ./dsh-ta-1.1.0.tgz
dsh --profile web --dump-config
```

Ta must be installed in `/Applications/拓.app` by default and granted macOS Screen Recording permission. Users can override `appPath`, `socketPath`, `timeoutMs`, `defaultCloud`, and `allowClipboard` in their profile patch.

Bridge v1 tools: `ta_system`, `ta_list_targets`, `ta_capture`, `ta_ocr`, `ta_analyze`, `ta_translate`, and `ta_deliver`. Transform, pin, long capture, and annotation tools will be registered only after those methods exist in the public Ta Bridge.
