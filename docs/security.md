# Security boundaries

Trayvora opens the microphone when the user invokes Record from the overlay, tray menu or hotkey. Capture is time- and size-limited. The recorder does not write audio to disk. When the user clicks Send, Codex receives the edited transcript or typed text and can save it in the dedicated profile next to Trayvora.exe. Its managed PCM and sample buffers are cleared after use.

The user starts the model download explicitly. Trayvora fetches the pinned multilingual Base model over HTTPS, checks size and SHA-256 and publishes only a complete model. Use `VOICEAGENT_MODEL_DIR` to locate the model outside the build directory. The portable build keeps models next to Trayvora.exe on any drive; development data for this project uses the Trayvora-Test directory.

The M0 inference probe used an ephemeral thread in an empty directory. It confirmed the built-in OpenAI provider and managed ChatGPT account, removed key and token fallback environment variables, and checked that shell, hooks, MCP, apps and plugins were disabled for that run.

The installed Windows sandbox reported `updateRequired`. The isolated profile reported `notConfigured`. The effective thread policy of `readOnly` is a configuration observation. Verification of OS-level read and write boundaries remains a release gate for workspace editing, arbitrary commands and write-capable MCP. See the [Windows sandbox documentation](https://learn.chatgpt.com/docs/windows/windows-sandbox).

M2 requires the managed ChatGPT account type and the built-in OpenAI provider. It removes fallback API credentials from the child environment, checks disabled tool features and configured MCP servers before sending, and rejects unexpected server requests by closing the connection. The app uses an empty working directory and requests read-only policy with approvals disabled. A dropped connection yields an unknown outcome and does not replay the input.

Trayvora saves submitted text and replies in a local, unencrypted SQLite history next to Trayvora.exe. The history stores generic failure summaries and does not store raw diagnostics or microphone audio. Deleting an entry affects the Trayvora index. The separate Codex profile has its own storage lifecycle.

Tool history records tool name or category and normalized status. Raw arguments, paths, commands and results are excluded. Windows notifications contain generic status text without prompt, answer or tool data.
