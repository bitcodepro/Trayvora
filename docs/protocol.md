# Codex App Server feasibility

Verified on 2026-11-14 against Windows build 26300 and the bundled `codex-cli 0.169.0-alpha.11.1`. Minimum supported Codex version remains to be determined.

| Check | Observed result |
| --- | --- |
| `codex login status` | Existing ChatGPT sign-in |
| `codex app-server --listen stdio://` | `initialize` succeeded, followed by `initialized` |
| `account/read`, `refreshToken: false` | `account.type = chatgpt`, `requiresOpenaiAuth = true` |
| `thread/start` | Ephemeral thread, disposable cwd, provider `openai`, effective `readOnly`, approvals `never` |
| `turn/start` | The prompt requested a single word. App Server emitted one `item/agentMessage/delta` and `turn/completed` with `completed`. |
| `windowsSandbox/readiness` | `updateRequired` for the existing profile, `notConfigured` for a fresh isolated profile |

Managed ChatGPT authentication worked when the server used the normal Codex profile and could access its runtime state. Under the restricted command sandbox, ordinary App Server startup failed to open SQLite. A writable SQLite override alone timed out with the existing profile. A fresh isolated home initialized but did not see the existing sign-in. These observations favor managed authentication for the local MVP. The alternative Sign in with ChatGPT provider requires a registered and eligible OAuth client and the documented token lifecycle. Its registration and inference were not tested.

The probe inherited no `OPENAI_API_KEY`, `CODEX_API_KEY` or `ACCESS_TOKEN`. It used startup overrides without saving config changes, and verified that all configured MCP servers were individually disabled before creating a turn. An empty override table by itself did not clear inherited MCP definitions. Do not use the shared configuration as an enforcement boundary for arbitrary user prompts.

The local CLI generated the checked-in schema fixtures with `codex app-server generate-json-schema --out <scratch-directory>`. Use them for the seed wire shapes. Validate against the installed executable when adding methods, including interrupt and approval responses.

- [App Server protocol](https://learn.chatgpt.com/docs/app-server)
- [Sign in with ChatGPT for App Server](https://developers.openai.com/siwc/token-sharing-open-source/codex-app-server)
- [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference)

## M2 desktop integration

The desktop starts an App Server for each submission, checks account and effective configuration before dispatch, and persists the returned thread ID for subsequent resume. It verifies that the thread is read-only, uses the OpenAI provider and cannot request approval. It accepts streamed deltas and terminal events for the active thread and turn IDs. On cancellation it requests interruption and waits up to ten seconds for the server to confirm a terminal status. If confirmation is lost, the outcome is unknown and the owned process is terminated.

A real App Server smoke test verified handshake and unauthenticated refusal with an empty dedicated E: profile. Live inference through this new profile requires signing in with ChatGPT. Automated tests use simulated completion and cancellation and an independent child-process transport test.
