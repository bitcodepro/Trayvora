# VoiceAgent — Implementation Plan

> **Status:** Implementation specification / agent execution checklist  
> **Target:** Windows 11 x64, Windows-first; .NET 10, Avalonia UI  
> **Product:** A local-first voice-controlled Windows tray agent powered by Codex App Server, with MCP integrations, Git, filesystem, PowerShell and application-launch capabilities.  
> **Primary auth goal:** Use the user's eligible ChatGPT subscription for Codex inference, **not** a separately billed OpenAI API key.  
> **Document language:** English (implementation instructions for coding agents).  
> **Last reviewed:** 2026-11-13

## 0. Instructions for the implementing coding agent

1. Read this entire document before coding. Treat **Security invariants**, **Definition of Done**, and **Out of scope** as binding requirements.
2. Implement incrementally, in the dependency order given. **Do not fabricate SDK interfaces, JSON-RPC methods, auth capabilities, or sandbox guarantees.** Verify against the currently installed Codex executable and official documentation before wiring them.
3. Begin with a **vertical slice**: tray → hotkey → record → transcribe → preview → Codex turn → display result. Prefer a working tested slice over many disconnected abstractions.
4. The default implementation MUST NOT require an OpenAI API key, a paid speech API, Electron, WPF, WinForms, a hosted backend, or administrator rights.
5. Ask for explicit approval before acquiring credentials, changing real repositories, pushing, merging, installing privileged software, or running destructive operations. Unit/integration tests must use fakes, sandboxes, and disposable test repositories.
6. After each milestone: build, run tests, fix warnings/errors, document tested behavior and limitations, and commit logically if repository permissions allow. Do not claim tests passed without executing them.
7. If ChatGPT-plan authorization cannot be validated on the installed Codex version, **stop that integration at a documented blocker**. Do not silently fall back to a separately billed API.
8. Do not expose actual secrets or credential values in logs, screenshots, crash reports, test fixtures or documentation.
9. Choose stable, maintained packages; pin compatible versions after verification. Avoid speculative package/version declarations in the implementation.
10. Keep `README.md`, `docs/architecture.md`, `docs/security.md`, and this checklist aligned with the implemented code.

## 1. Product goals

### 1.1 Core user experience

- VoiceAgent starts with Windows (optional, off by default) and runs **in the system tray** without a permanent main window.
- A configurable global hotkey (`Ctrl+Alt+Space` suggested) toggles microphone recording. Default interaction: press to start; press again to stop. Also offer hold-to-talk when feasible.
- A small, keyboard-friendly Avalonia overlay shows recording state, waveform/level, transcription preview, progress, approval requests, and result.
- Speech is transcribed **locally**, with Russian, Ukrainian and English supported; editable transcription is sent as text only after explicit user action or enabled setting.
- Codex executes tasks using available tools and MCP servers, streams progress, and keeps per-thread history.
- The user can cancel, reject/approve specific actions, open detailed history, inspect tool actions and change task workspace/profile.
- The application remains responsive if speech recognition, Codex or MCP processes hang or crash.

### 1.2 Example workflows (acceptance scenarios)

1. “Analyze the latest pull request in my selected GitHub repository” → read-only GitHub MCP → summary with file/line references, no repository mutation.
2. “Run tests for this solution” → configured trusted workspace → bounded test invocation → show exit status and concise result.
3. “Find the Redis configuration in this project” → allowed filesystem → file locations, no disclosure of secrets.
4. “Open Visual Studio for this solution” → launch **allowlisted** executable with validated file path.
5. “Review PR 58 and comment on the bugs” → read, analyze, then show **exact outgoing comments** and require confirmation before posting.
6. “Merge PR 58” → explicit one-time confirmation showing repository, branch and PR; no broad approval.
7. “Delete all files” → deny by default, or provide a narrow reviewed plan under an independently enforced privileged mode; never run silently.

### 1.3 Non-goals for MVP

- Reimplementation of Codex's internal agent loop or model inference.
- Synchronization with ChatGPT conversations or existing cloud Codex tasks (not guaranteed by App Server).
- Always-on wake word, passive background recording, cloud speech recognition or voice synthesis.
- Fully autonomous administrative access, unlimited PowerShell access, arbitrary remote execution or unattended destructive work.
- Generic GUI click automation/desktop computer use (future phase only).
- Automatic installation of arbitrary untrusted MCP packages.
- Remote web dashboard, phone companion, multi-user/server-hosted product, enterprise tenancy.

## 2. Technology decisions

| Layer | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 LTS, Windows x64 | Keep platform APIs behind interfaces. |
| UI | Avalonia UI, Avalonia desktop | **No WPF / WinForms / Electron**. |
| UI pattern | MVVM, CommunityToolkit.Mvvm | Dependency injection, reactive state updates on UI thread. |
| Tray | Avalonia `TrayIcon` + `NativeMenu` | Define at application scope; survive hiding overlay. |
| Global hotkey | Windows `RegisterHotKey` + dedicated message pump/window interop | Abstract `IGlobalHotkeyService`; handle registration failure/conflicts. |
| Audio capture | NAudio / Windows WASAPI | Device selection, 16 kHz mono PCM conversion for STT if model requires. |
| Offline STT | Whisper.net with supported whisper.cpp native binaries | Model download only with explicit consent; document CPU/RAM/disk. |
| Agent | `codex app-server --listen stdio://` | JSONL bidirectional protocol, separate child process. |
| Auth | Codex-supported ChatGPT sign-in, verified in integration spike | Never automatically switch to paid API key. |
| Tools | Codex native tools and Codex MCP integration | Avoid duplicate MCP agent execution layer. |
| State | SQLite for app-owned tasks/metadata; JSON for settings | Store only necessary content; enable history deletion. |
| Secrets | Windows Credential Manager or user-bound DPAPI | Prefer Codex-managed auth storage when supported. |
| Logging | Microsoft.Extensions.Logging + Serilog provider | Mask secrets, omit raw audio and sensitive payloads. |
| Tests | xUnit, test doubles, disposable temp dirs/repos | Integration tests must not mutate actual user data. |
| Packaging | Windows x64 self-contained publish (evaluate size) | Optional MSIX later; code signing optional before release. |

### 2.1 Deliberately separated concerns

- **Desktop UI** renders state and solicits user decisions. No direct file/Git/shell side effects from ViewModels.
- **Application orchestration** manages task state, cancellation, concurrency, approvals and persistence.
- **Codex transport** is a protocol adapter and process supervisor; not an LLM provider abstraction pretending every backend is interchangeable.
- **Speech subsystem** handles devices, audio buffers, transcription and model lifecycle.
- **Execution boundaries** (workspace selection, sandbox, network, credentials) are enforced separately from prompts/UI.

## 3. Proposed solution layout

```text
VoiceAgent/
├─ VoiceAgent.slnx
├─ global.json
├─ Directory.Build.props
├─ README.md
├─ plan.md
├─ AGENTS.md
├─ docs/
│  ├─ architecture.md
│  ├─ protocol.md
│  ├─ security.md
│  ├─ setup.md
│  └─ troubleshooting.md
├─ src/
│  ├─ VoiceAgent.Desktop/            # Avalonia views, tray, overlay, Win32 interop
│  ├─ VoiceAgent.Application/        # app use cases and state machine
│  ├─ VoiceAgent.Domain/             # entities, task states, policy contracts
│  ├─ VoiceAgent.Codex/              # process lifecycle, JSONL RPC, event adapter
│  ├─ VoiceAgent.Speech/             # recording, transcoding, STT adapter
│  ├─ VoiceAgent.Security/           # approvals, profiles, sanitization
│  └─ VoiceAgent.Infrastructure/     # SQLite, settings, diagnostics
├─ tests/
│  ├─ VoiceAgent.UnitTests/
│  ├─ VoiceAgent.IntegrationTests/
│  └─ VoiceAgent.E2ETests/           # opt-in smoke tests requiring local Codex
├─ assets/
│  ├─ icons/
│  └─ models/README.md              # no model binaries in git
└─ scripts/
   ├─ build.ps1
   ├─ test.ps1
   └─ publish-win-x64.ps1
```

### 3.1 Dependency direction

`Desktop → Application → Domain`  
`Codex, Speech, Security, Infrastructure → Application/Domain contracts`  
No reference from Domain or Application to Avalonia types. Infrastructure never invokes UI. Create a composition root in Desktop.

### 3.2 Essential abstractions

```csharp
public interface IAudioRecorder
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<RecordedAudio> StopAsync(CancellationToken cancellationToken);
    Task CancelAsync(CancellationToken cancellationToken);
}

public interface ISpeechTranscriber
{
    Task<TranscriptionResult> TranscribeAsync(
        RecordedAudio audio, CancellationToken cancellationToken);
}

public interface IAgentSessionService
{
    Task<AgentThread> StartThreadAsync(AgentRunOptions options, CancellationToken ct);
    IAsyncEnumerable<AgentEvent> RunTurnAsync(
        string threadId, string text, CancellationToken ct);
    Task InterruptAsync(string threadId, CancellationToken ct);
}

public interface IApprovalService
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

public interface IWorkspacePolicy
{
    WorkspaceDecision Check(string requestedPath, WorkspaceOperation operation);
}
```

These are **application-owned sample contracts**, not claims about Codex SDK types. Adapt exact DTOs only after generating/inspecting installed App Server schemas.

## 4. User interface specification

### 4.1 Tray

- Single tray icon with tooltip reflecting `Idle`, `Recording`, `Transcribing`, `Running`, `Awaiting approval`, `Error`.
- Context menu: **Record**, **Type a command**, **Recent tasks**, **Settings**, **Pause hotkey**, **Exit**.
- Exit explicitly cancels or asks how to handle running work; never silently abandon pending approvals.
- Starts hidden; closing overlay hides it, not the app. App shutdown exits the tray cleanly.
- Prevent duplicate instances (per-user named mutex); second invocation focuses overlay via a safe local signal.

### 4.2 Floating overlay

- Small always-on-top optional non-activating/keyboard-focusable window near screen bottom/top, respects taskbar/work area and multi-monitor DPI.
- States: `Idle → Recording → Transcribing → Preview → Sending → Running → Completed | Failed | Cancelled`; `AwaitingApproval` can interrupt Running.
- `Escape`: cancel recording or hide overlay depending on state; do **not** automatically cancel remote work on hide.
- Buttons: Record/Stop, Edit, Send, Cancel, Open details.
- Audio peak indicator (not a misleading synthetic waveform); timer; explicit mic on/off visual.
- Editable preview defaults to manual send for MVP; user may opt in to auto-send on recognized speech.
- Accessible labels, high-contrast, keyboard navigation, reduced-motion option.

### 4.3 Task detail/history

- Timeline of user input, assistant streamed messages, actual tool events, results and approvals.
- Visible task `Working directory`, `Profile`, `Codex thread`, timestamps and final status.
- Distinguish *completed* from *interrupted* and *failed*. Do not label failed work success.
- Show diff before changes/commits where protocol exposes it; where unavailable, explicitly state not available.
- Keep SQLite index and selected metadata; do not blindly duplicate every full Codex transcript.
- Settings allow retention limit, clear history and model downloads deletion.

### 4.4 Settings

Tabs: General, Voice, Codex, MCP, Workspaces, Security, Diagnostics.

- General: startup on login, hotkey, tray behavior, display notifications.
- Voice: mic device, language auto/RU/UK/EN, model size, download status, push-to-talk mode.
- Codex: executable detection/version, auth status, model selection, connection health, test turn.
- MCP: list available/configured servers, connection test, allowed tools, permission sensitivity. Show provenance of config.
- Workspaces: trusted roots and default selected repo, with path canonicalization.
- Security: selected profile, approval history, sandbox status, dangerous capabilities clearly flagged.
- Diagnostics: sanitized export, logs, Codex stderr, dependency health.

## 5. Speech pipeline

### 5.1 Recording

1. Register hotkey at app startup; surface conflict and allow remapping.
2. On activation, check consent and microphone availability; open selected capture device.
3. Capture PCM data via bounded in-memory buffers, cap maximum recording length and size (configurable, safe default).
4. On second activation, stop capture and convert/normalize as required by chosen Whisper model.
5. Transcribe in cancellable background task. Ensure native resources and audio buffers are released.
6. Display result for correction, followed by manual Send (default).
7. Do not persist raw audio unless the user has explicitly enabled a local diagnostic setting; delete temporary audio after use.

### 5.2 Models

- Support one small/default multilingual Whisper model for MVP; allow later change of model.
- Model download: explicit URL/version/checksum, user confirmation, progress, retry, disk-space validation.
- Validate checksum, protect against partially downloaded binaries and arbitrary model filenames.
- Handle cold-start latency and hardware constraints gracefully; background-load model on demand or optional preload.
- Implement normalization for RU/UK/EN without altering code identifiers, branch names, paths or quoted commands unexpectedly.
- Treat transcription as **untrusted** input: the user must be able to review sensitive actions. No high-risk action is authorized merely because it was spoken.

### 5.3 Failure handling

- No microphone, exclusive device, unplug during recording, silence, unsupported model, native DLL missing, memory pressure, cancelled STT, long transcription.
- No infinite retry loops or unbounded recordings. UI stays responsive.

## 6. Codex integration

### 6.1 Required architecture

- Spawn locally installed `codex app-server --listen stdio://` as **non-elevated** child process.
- Redirect stdin, stdout and stderr; write **one JSON object per line** to stdin; read stdout continuously.
- Protocol is JSON-RPC-like, with `jsonrpc` header omitted on wire. Preserve `id`, `method`, `params`, `result`, `error` and server-initiated requests.
- Serialize requests with unique IDs, maintain pending-request map, cancellation/timeout policy, bounded event channel and orderly shutdown.
- Process stdout is a protocol stream, **never** mix its contents with logging; collect stderr separately with sensitive-data redaction.
- Use `System.Text.Json` with resilient parsing and explicit unknown-message handling; never assume events arrive once or in a specific UI order beyond protocol guarantees.
- Start and wait for `initialize`; send `initialized` notification; then create/resume thread.
- Create turn with `turn/start`; process item started/delta/completed, approval requests, tool progress and final `turn/completed`.
- Check terminal status: only `completed` is success; preserve `failed` and `interrupted` accurately.
- Support interruption using documented `turn/interrupt`, and ensure UI cancellation does not merely cancel an awaiting C# Task while work continues unobserved.
- Resume known threads using `thread/resume` when supported and valid; reconcile stale task state on launch.
- Respect `cwd`/workspace, sandbox configuration and model settings determined from verified schemas.

### 6.2 Auth and subscription — critical validation gate

**Never use an API key as a hidden fallback.** Two implementation paths must be explicitly investigated:

**Path A (preferred MVP): existing official Codex authentication**

- Detect installed Codex CLI and version; check `codex login` / authenticated status using documented commands on that version.
- Offer “Sign in with ChatGPT” via supported Codex CLI flow and use the same session if App Server recognizes it.
- Test a harmless inference turn and confirm the user is authenticated under their eligible ChatGPT plan.
- Never read/write private Codex credential files directly unless officially supported.

**Path B (if needed and actually available to this app): Sign in with ChatGPT OAuth**

- Review current official SIWC App Server guidance; validate app registration / client eligibility and OAuth PKCE flow before implementing UI.
- If credentials are issued for the app, exchange/refresh securely, store via user-bound secure storage, and configure App Server as officially documented.
- Official SIWC documentation currently describes an `ACCESS_TOKEN` child-process environment variable and a `openai_chatgpt_plan` model-provider configuration; verify exact parameters on the supported Codex build.
- Token expiry/revocation must fail closed; restart App Server safely with renewed token and `thread/resume` if needed.
- Do **not** assume OAuth registration or token-sharing is universally available to arbitrary unpublished apps.

**Success criterion:** actual successful `turn/completed` with user plan, without setting `OPENAI_API_KEY` or charging a separate OpenAI API account. A catalog entry from `model/list` alone does not prove entitlement.

**If both paths fail:** show a clear blocked state and documentation; do not continue toward a paid provider without user opt-in and a separate plan revision.

### 6.3 Versioning and protocol discovery

- Record tested Codex version and minimum compatible version in docs.
- Obtain protocol schemas from official Codex App Server references or Codex's schema-generation tooling where available.
- Protocol decoder must tolerate additional event types; log unknown type **names** but not secrets.
- Add contract tests against recorded **sanitized** protocol fixtures.
- Do not hardcode a model ID; populate supported options by capability discovery and validate with an actual test turn.

### 6.4 Concurrency and lifecycle

- MVP: **one active Codex turn at a time** (queue subsequent tasks with clear UI). Avoid cross-thread mixing.
- Bounded queue, cancellation, backpressure; no unbounded memory growth on streamed deltas.
- On process crash: mark task interrupted/unknown; restart with exponential backoff and a maximum retry count; do not blindly re-run a mutating turn.
- On app shutdown: prompt on running work, interrupt if authorized, dispose streams, kill child process tree when necessary and allowed.
- Never assume tool action can be rolled back after cancellation.

## 7. MCP support

### 7.1 MVP scope

- Reuse **native Codex MCP configuration/integration**; do not implement a second general-purpose MCP client to route the same agent calls.
- First integrations: one read-only example/docs MCP and GitHub MCP through documented supported configuration.
- Support local stdio servers and remote Streamable HTTP servers as capabilities provided by installed Codex.
- Provide UI that surfaces current configuration. Configuration changes must respect source-of-truth and avoid destructively overwriting shared `~/.codex/config.toml` settings.
- Prefer a **dedicated VoiceAgent Codex profile/config home** if supported and tested; otherwise build a merge-safe config editor, backup, diff and rollback.
- Display discovered tools and per-tool risk category where protocol makes discovery available; clearly state when metadata is not exposed.
- Never auto-run an arbitrary MCP command supplied by untrusted text; installing/configuring a server is a privileged human operation.
- Remote MCP OAuth/token configuration belongs in protected credential storage, not plain exported JSON.

### 7.2 Threat model

- MCP server descriptions, tool output, GitHub PR text, repository README, web pages and transcribed speech are all **untrusted data**; they cannot override system-level policy.
- Tool integrations may have broader credentials than the agent's workspace. Least-privilege scopes are mandatory.
- A model's request for a tool call is not automatically allowed by the fact the tool exists.
- Do not equate generic shell-tool approvals with safe MCP approvals. Where App Server executes an MCP action internally without giving the app a pre-execution interception point, enforce restrictions **outside** the model: server credentials, tool allowlists, proxy/broker, read-only server variants, or simply disable that integration.

## 8. PowerShell, Git, files and application launches

### 8.1 Three execution profiles

**ReadOnly (default at first launch):**

- Select one workspace; allow reads through a genuinely read-only interface, and no generic shell.
- Read-only Git operations (status/log/diff) only through controlled operations or a verified sandbox.
- MCP server permissions read-only where supported; write-capable tools disabled or credentials scoped to read only.

**WorkspaceWrite (opt-in):**

- Codex may edit files within the **actual OS-enforced restricted workspace**. Protect other user folders, credentials, home directory and system paths.
- If Windows sandbox implementation cannot enforce the boundary for available Codex tools, do not label this profile sandboxed; downgrade to ReadOnly or require a dedicated isolated VM/workspace.
- Test runners allowed only when execution environment isolation is adequate; build/test scripts can execute arbitrary code.

**Advanced (explicit opt-in, future/limited MVP):**

- Higher-risk shell, Git push, app launches, remote write MCP and wider permissions require purpose-specific approval and constrained capability scopes.
- No blanket persistent “always allow everything” switch. No automatic elevation.
- A user approving a specific action authorizes **only that exact canonical action and arguments** for the displayed target.

### 8.2 Safe operations API

Consider app-owned narrow operations for selected actions, executed by a trusted broker rather than from arbitrary text:

- `GitStatus(repoId)`, `GitDiff(repoId, ...)`, `GitLog(repoId, ...)`
- `OpenSolution(allowedSolutionId)`, `OpenUrl(allowlistedUrl)`, `RunConfiguredTests(projectId)`
- `ReadFile(workspaceId, relativePath)` with canonical-path and file-boundary enforcement.

If exposing these through an application-owned MCP, use a **distinct, permission-aware MCP server** with narrow schemas. Do not describe a generic shell MCP as “safe” solely because a wrapper checks a few forbidden command substrings.

### 8.3 Filesystem boundaries

- Normalize and resolve absolute paths; prevent `..`, UNC/device paths, junctions/reparse-point escapes, symlink swaps and time-of-check/time-of-use issues.
- Verify **effective** access at execution time, not only at UI selection time.
- A path allowlist is **not an OS sandbox**; unrestricted child processes running as the Windows user may still read other user files.
- Use a separate OS security identity, VM, Windows Sandbox or a rigorously verified restriction mechanism for stronger isolation. Explicitly document what cannot be enforced on Windows.
- Keep auth material and audit database outside agent-writable workspace.

### 8.4 PowerShell boundaries

- Safe profile: no arbitrary `pwsh.exe`, `powershell.exe`, `cmd.exe`, scripting engine or shell command supplied by model.
- Advanced profile: require one-time approval, exact script/command/working directory/arguments, and run within an enforced scope. Guard against aliases, encoded commands, indirect script execution, child processes and network egress.
- Do not claim regex, blocklists or PowerShell execution policy provide a security boundary.
- Set timeouts, resource bounds and process-tree cancellation where enforceable.

### 8.5 Git boundaries

- Read-only repo analysis should not change user branch or index.
- Git hooks, config aliases, submodules and build scripts can execute code; assess before running untrusted repository commands.
- For write workflows use disposable worktrees/branches when possible; present diff before applying back.
- Push/force push/branch deletion/PR merges require explicit confirmation of target repository, branch and remote, with exact proposed change.

### 8.6 Application launch

- MVP supports opening a specific selected application or solution using a **user-maintained allowlist**.
- Resolve executable to a known full path; validate arguments, target file and execution identity.
- Do not allow arbitrary named executables from PATH or shortcuts from untrusted content.
- Generic UI Automation is deferred.

## 9. Approval system and safety invariants

### 9.1 Non-negotiable invariants

1. No elevated/admin execution by default.
2. No always-listening microphone without an explicit opt-in in a future feature.
3. No paid OpenAI API fallback without explicit agreement.
4. No automatic destructive, publishing, remote write or credential-related action based solely on voice/model output.
5. No arbitrary shell inside ReadOnly/Safe Workspace modes.
6. No reliance on prompt instructions to block filesystem, credentials or network access.
7. Any unavailable approval handler or unclear tool impact must **fail closed**.
8. “Cancel” and “deny” must be enforceable; a denied action never proceeds through an alternate unguarded route.
9. Raw auth tokens, private keys and raw audio are not logged.
10. Only grant the minimum OAuth/MCP scopes necessary for the chosen task.

### 9.2 Approval policy

| Capability | ReadOnly | WorkspaceWrite | Advanced |
|---|---|---|---|
| Analyze authorized repo | Allow | Allow | Allow |
| Edit sandboxed workspace | Deny | Allow within enforced boundary | Allow within enforced boundary |
| Run arbitrary shell | Deny | Deny | Specific approval + isolation |
| Run configured tests | Deny or trusted isolated | Allowed if isolated | Allowed if isolated |
| Create commit | Deny | Confirm | Confirm |
| Push / merge / delete branch | Deny | Confirm + proper tool scope | Confirm + proper tool scope |
| External write MCP | Deny | Confirm + narrow credential scope | Confirm + narrow credential scope |
| Read secrets outside workspace | Deny | Deny | Deny by default |
| Install software / privilege escalation | Deny | Deny | Out of scope for MVP |

The table represents *desired effective policy* and must not be exposed as true until enforced through OS boundaries, dedicated constrained tools/credentials, and verified Codex sandbox approvals.

### 9.3 Request dialog

Display exact tool name, provider, target, cwd, safe summary, full arguments or diffs (with secret redaction), and irreversible effects. Buttons: Deny, Approve Once (if supported), View Details. Confirmation expires; timeout is Deny. Bind a decision to exact request ID and server instance; prevent reuse/replay and double approval.

### 9.4 Emergency stop

- UI Cancel → `turn/interrupt`, then monitor terminal state.
- Tray Emergency Stop → reject all pending approvals, stop accepting new work, interrupt, and terminate owned process tree if necessary.
- Some actions may have already committed. Show explicit outcome **unknown** if uncertain; do not claim rollback.

## 10. Domain/state model

### 10.1 Entities

- `AgentTask`: `Id`, `ThreadId`, `TurnId`, `CreatedAtUtc`, `StartedAtUtc`, `CompletedAtUtc`, `Status`, `WorkspaceId`, `Profile`, `PromptPreview`, `ErrorCode`.
- `AgentEvent`: `TaskId`, `Sequence`, `TimestampUtc`, `Type`, `SanitizedPayloadSummary`.
- `Workspace`: `Id`, `DisplayName`, `CanonicalRoot`, `Trusted`, `Profile`.
- `McpServerEntry`: `Id`, `Name`, `Source`, `Transport`, `Enabled`, `ToolAllowlist`, `Status` (no raw secrets).
- `ApprovalRecord`: `Id`, `TaskId`, `ToolId`, `CanonicalTarget`, `Decision`, `DecisionUtc`, `ExpiryUtc`, `RedactedSummary`.
- `UserSettings`: hotkey, microphone id, STT model id, retention, startup, notifications, default profile.

### 10.2 States

```text
Idle -> Recording -> Transcribing -> Preview -> Queued -> Starting -> Running
  Recording -> Cancelled
  Transcribing -> Cancelled | Failed
  Running -> AwaitingApproval -> Running
  Running -> Completed | Failed | Interrupted | Unknown
  Queued -> Cancelled
```

State transitions must be serialized through an application service. Never write UI state directly from an async event handler on a background thread.

### 10.3 Persistence

- Use EF Core SQLite or lightweight `Microsoft.Data.Sqlite`; choose based on actual complexity.
- Migrations and versioned settings required.
- History may contain sensitive user text: local-only, configurable retention and deletion. Optional at-rest protection for sensitive fields, based on Windows user-bound key; avoid claiming SQLite is encrypted by default.
- DB and logs reside under user app data, never in the repository workspace.

## 11. Diagnostics, performance and reliability

### 11.1 Baselines (targets, not guarantees)

- Idle: no audio capture, no unnecessary inference calls, low CPU; measure RAM/CPU and optimize later.
- Hotkey-to-visible-overlay: target perceptually immediate (e.g. < 200 ms on test machine).
- Responsive UI while recording, loading model, transcribing, or streaming.
- No recorded raw audio retained by default.
- Bounded recording duration, event queue, logs and task history size.
- Explicit degraded/error state when no network, no Codex login or MCP unavailable.

### 11.2 Logging

- Structured events with correlation IDs for task/thread/turn/process session.
- Mask bearer tokens, passwords, auth headers, env secrets, authorization codes, private URLs and sensitive file contents.
- Never log full MCP payloads by default; default to provider/tool name, duration, status and sanitized target.
- Export diagnostic bundle requires user confirmation and displays included file list.

### 11.3 Lifecycle/recovery

- Crash-safe SQLite writes, process cleanup and orphan detection.
- After app restart, recover known thread metadata, mark in-flight tasks `Unknown` until reconciled. Do not repeat side-effecting commands automatically.
- Handle system sleep/resume, hotkey re-registration if needed, microphone device changes and Windows session transitions.

## 12. Implementation milestones and acceptance gates

### M0 — Foundation and feasibility (must finish first)

- [x] Create solution/projects, central NuGet management, linting/formatting, DI and test harness.
- [ ] Confirm working Avalonia `TrayIcon` and native context menu on Windows 11.
- [x] Verify `RegisterHotKey` integration on actual Windows (message loop and conflict behavior).
- [x] Detect locally installed Codex CLI, record version, verify `codex app-server --listen stdio://`.
- [x] Read current official App Server protocol / auth docs, generate or copy actual schemas into test fixtures as allowed.
- [x] Verify **real ChatGPT-subscription inference** with a harmless test prompt and no API key fallback.
- [x] Record exactly which subscription/login flow works and any account/registration dependencies.
- [x] Identify Windows sandbox capabilities available in selected Codex version; document known gaps.
- **Gate:** If App Server + eligible ChatGPT auth doesn't work, do not fake it; report blocker and preserve working shell/UI spike.

**M0 evidence (2026-11-14):** .NET SDK 10.0.401 and Avalonia 12.1.3. On Windows build 26300, restore from nuget.org succeeded, the solution built with zero warnings and zero errors, and the Win32 registration/conflict/release test passed (1/1). The tray process stayed alive with no visible main window during a startup smoke test. An additional test delivered an injected WM_HOTKEY through the actual Windows message queue. A visual tray/menu check, physical shortcut press remain open. The existing ChatGPT login completed a real streamed turn, and native Windows sandbox readiness returned `updateRequired`. See [verification details](docs/protocol.md).

### M1 — Tray and microphone

- [ ] Start hidden in tray; Exit actually terminates app.
- [x] Overlay starts hidden and closes to the tray on Escape. Verified on the Windows 11 desktop window.
- [x] Hotkey configurable; collisions shown to user.
- [x] User verified real microphone dictation. The recorder includes a device picker, peak meter, duration limit and memory-only PCM.
- [x] Local Whisper transcribes spoken RU/UK/EN after explicit model download.
- [ ] Editable transcript and Send button; cancel works in all stages.
- **Gate:** A voice command becomes user-editable text without Codex/network usage.

**M1 shortcut settings:** The tray context menu opens an Avalonia Settings window. The microphone shortcut defaults to Ctrl+Shift+Z and can be changed live. If registration or saving fails, the previous shortcut remains active. The settings file is versioned and resides outside C:. The owner will test the tray and microphone interactively.

**M1 progress (2026-11-14):** Added a WASAPI microphone picker, recording start and stop, peak indicator and 30-second cap. Captured PCM stays in memory. Added local Whisper Base inference, an editable transcript and cancellation. The pinned model was downloaded with consent to Trayvora-Test and verified by SHA-256. Local tests matched known English and Russian SAPI fragments and a Ukrainian phrase from FLEURS. Whisper misrecognized some Ukrainian words. The microphone-to-overlay flow and native tray menu still need interactive checks. Speech settings support an editable local Whisper prompt for product names, repository names and Git phrases. Base remains the default. The floating agent uses a persistent styled icon and reflects recording, transcription and submission. The preview shortcut displays processing and an editable result near the top of the monitor. A Windows UI test checks the preview and icon states with synthetic capture. Send and cancellation are implemented in M2. New build artifacts, NuGet caches, model and test data use Trayvora-Test on E:.

### M2 — Codex transport and task lifecycle

- [x] Process host, JSONL request/notification router, stderr collector.
- [x] Initialize/initialized handshake and thread/start.
- [x] `turn/start` with text; stream message events; show final status.
- [x] Interrupt active turn; robust unexpected process exit handling.
- [x] Resume thread after restart where supported.
- [x] Single-active-turn queue and full cancellation semantics.
- [x] Auth failure is clearly reported; no API key fallback.
- **Gate:** Voice → text → Codex → streamed completion works with subscription.

**M2 verification:** The window sends typed or edited text explicitly and streams the response. Cancel sends turn/interrupt and waits for the matching terminal status. The adapter serializes execution and admits one waiting request, while the single overlay disables Send until the active request ends. Tests cover resume, streaming, interruption, failed and unknown outcomes, policy/auth rejection, and JSONL over a real child process. A real installed App Server accepted the handshake and refused the unsigned E: profile before dispatch. The full live M2 gate awaits user sign-in in the dedicated E: profile and a UI smoke test. M0 verified live subscription inference with the existing Codex profile. Manual tray and microphone checks remain with the owner.

### M3 — History and task UX

- [x] SQLite task index and event summaries with migrations.
- [x] Recent tasks view, task details, statuses, inspect tool names.
- [x] Windows notification on completion. Approval notifications depend on the future approval flow.
- [ ] Clean handling of sleep, resume, crash and invalid stored thread IDs.
- [x] Diagnostic payload exclusion verified with a synthetic secret in stderr and approval payloads.
- **Gate:** User can follow one task from submission through completion or failure and reopen it.

**M3 evidence:** SQLite schema v2 preserves existing prompts and answers. History reports bounded tool name/category and status. It excludes tool arguments, commands, paths and output. Windows Shell accepted a generic notification from an invisible VoiceAgent window in an automated test. The test sends suspend, automatic resume and interactive resume messages through that window and verifies resume is handled once. The app cancels active operations, refreshes microphones when available and never replays an old turn. A physical sleep/wake cycle, on-screen notification checks and invalid stored thread recovery still need manual acceptance. Tool-enabled profiles and approval UI remain later milestones.

### M4 — MCP and safe local actions

- [x] Display configured Codex MCP servers and integrate Revqora with a read-only tool allowlist.
- [ ] Revqora provides PR reads for connected repositories. Verify live read-only access through the owner's profile.
- [x] Local workspace picker, canonical paths, and visible read-only Codex profile.
- [x] Local Git branch/HEAD reads verified on a disposable repository.
- [x] Open the selected folder through the fixed Windows File Explorer executable.
- [x] Read-only approval requests receive explicit denial; blocked actions are shown in History and the active window.
- [x] Verified denied command/file/permission requests and inspected the live App Server catalog: only read-only Revqora tools were exposed. No automatic direct Git fallback.
- **Gate:** Read-only analysis of a real selected repo, no unauthorized mutations.

The selected application integration is Revqora. Automatic routing uses its MCP for recognized repository requests. A direct local Git route must be selected explicitly. A separate local action initializes only a selected empty folder. The Revqora route allowlists read-only tools and does not expose request_action. Live account, repository access and approval acceptance remain open.

M0–M4 acceptance: Windows 11 automated checks cover hidden startup, hotkey delivery, speech fixtures and power notifications. Live microphone recording, physical sleep/wake and sign-in to the dedicated ChatGPT profile still need interactive acceptance. Revqora tool discovery and list_repositories worked on the installed server. Live PR reading from the signed-in desktop profile remains open. Simulated command, file, permission and MCP requests are denied with sanitized history entries. The real read-only MCP tool allowlist was verified separately. Full denial bypass acceptance against live tools remains open. Approval that enables writes belongs to M5.

Follow-up acceptance: native Windows 11 tests cover overlay show/hide, editable transcription with a test microphone, Send control availability, and recovery from a simulated suspend during recording. Live Revqora reported repository status successfully and returned provider_authentication_failed on the selected repository PR list. The application now checks the connected Revqora catalog before dispatching a model turn, and refuses the turn if any unapproved tool is exposed. Live Revqora PR authentication, separate ChatGPT profile login, physical microphone and sleep tests remain open.

Chat follow-up: VoiceAgent stores named chat entries and the active selection locally. Codex and Revqora turns resume the same Codex thread for the selected chat. The original threads are available separately, and older request rows remain visible in all-chat History.

Desktop dictation adds a dedicated shortcut that inserts recognized text in the focused Codex Desktop window for manual submission. The draft can be copied from Trayvora if the original target is unavailable.

Shared Desktop chat follow-up: Trayvora discovers chats through Codex App Server and submits to the selected thread queue. It matches the response by the submitted client message ID, can open the chat in Desktop, and can read its text aloud through Windows SAPI. Codex Desktop runs the turn under that chat's settings. The desktop UI loads paginated chat results on first expansion, filters by title, and includes an editable response field for playback testing. Playback supports a configurable Windows voice and rate, or OpenAI speech with opt-in API billing. The playback provider can reuse the saved recognition key or use its own key.

Trayvora also supports general questions with a low-cost OpenAI text model, short in-memory context and a persistent long conversation with older turns summarized and optional playback. This mode sends text only and does not depend on a running Codex process.

Windows audio capture is available as a separate configurable loopback shortcut. It captures only after explicit activation and uses the same duration cap and transcription pipeline as microphone capture.

**Deferred: Follow Active IDE Session**

- [ ] Route voice submissions to the active Codex or Claude Code session in VS Code.
- [ ] Outside VS Code, fall back to the last active session selected in Trayvora.
- [ ] Verify session identity and use the existing interactive session so two processes do not operate on the same Claude chat simultaneously.

Until this is implemented, select the route manually in Trayvora and close the interactive Claude session before sending to it from Trayvora.

### M5 — Workspace editing and approvals

- [ ] Select and verify a **real enforceable** Windows sandbox/execution boundary.
- [x] Codex workspace editing remains disabled pending verification of runtime isolation.
- [ ] Controlled write actions and diff visibility.
- [ ] One-time approval for commit/push/remote write where available and enforceable.
- [x] Emergency stop terminates the owned App Server, and its invocation is recorded in task history.
- [ ] Injection tests: malicious README/PR/MCP output cannot change allowed operations.
- [ ] No raw credential access in agent execution environment.
- **Gate:** A deliberate attempt to escape workspace or run forbidden tool fails in an automated/tested scenario.

### M6 — Packaging and release

- [x] Self-contained Windows x64 archive includes the Whisper native runtime. The model remains a separate verified download.
- [x] Portable setup guide covers dependencies, login, model and Revqora.
- [x] Optional startup script manages one installation-owned HKCU Run entry; portable removal is documented.
- [ ] Integration smoke suite on clean Windows 11 VM.
- [ ] README with screenshots, security disclaimer, limitations and troubleshooting.
- [x] Portable Windows archive with an independently verified SHA-256 checksum.
- **Gate:** Clean Windows 11 user can launch, log in, set hotkey, transcribe and run a harmless task.

### Ten implementation and verification passes

1. Audited the pending gates and restored the staged source tree on E:.
2. Restored orderly tray Exit and recovered malformed saved thread IDs.
3. Added and tested emergency termination of a hung owned App Server.
4. Added canonical local workspace selection and fixed File Explorer launch.
5. Added direct Git branch/HEAD inspection without metadata changes.
6. Added explicit Git initialization for an empty selected folder.
7. Added the Revqora-first route with a separate thread and no local fallback.
8. Verified live Revqora discovery and App Server read-only tool configuration.
9. Added the Windows x64 portable package and setup guide.
10. Ran 46 tests, checked bundle contents and SHA-256, and launched the portable executable.

### Follow-up after owner testing

1. Record owner acceptance of physical dictation and repository discovery.
2. Verify sign-in to the existing isolated E: profile via CLI status.
3. Verify real streamed Codex response using that profile.
4. Verify live Codex-to-Revqora tool activity.
5. Check Windows sandbox readiness and preserve the read-only mode if unconfigured.
6. Check the MCP allowlist against write and UI-navigation tools.
7. Record emergency stops in task history.
8. Prevent duplicate desktop launches from the same installation.
9. Package the ready launchers, verified Base model and optional startup controls.
10. Passed all 53 tests; verified SHA-256, startup and duplicate-instance protection in the Windows x64 package.

Acceptance outcome: the signed-in profile streamed a real reply and called Revqora list_repositories. The live PR tool call failed and remains open. Windows sandbox readiness is notConfigured in this profile. Hyper-V inventory access was denied by Windows, so clean VM acceptance remains open. The optional startup script has not been enabled on this machine.

## 13. Test plan

### 13.1 Unit tests

- Task state transition legality, invalid transition rejection, event ordering.
- Request ID correlation, JSONL framing, malformed message, unknown notification, partial line handling.
- Transcript normalization does not corrupt source code fragments, URLs, paths or identifiers.
- Hotkey mode state transitions and duplicate action suppression.
- Approval exact-request binding, expiry, deny-on-timeout, replay attempts.
- Path policy: traversal, junction, symlink, UNC and other Windows edge cases (supplement with OS tests).
- Sensitive-data redaction including nested JSON and stderr.
- Settings migration and history retention.

### 13.2 Integration tests

- Fake App Server child process: initialization, streaming, approval request, error, interruption, abrupt exit.
- Actual Codex CLI smoke tests **opt-in**; no user credentials in CI.
- NAudio device tests opt-in; unit tests on synthetic PCM.
- Disposable Git repo: read-only operations don't mutate working tree; malicious repo hooks aren't blindly executed.
- Mock MCP: read-only, timeout, malformed tool output and attempted prompt injection.
- Sandbox boundary: demonstrate denied access to outside test directory and fake credential file.
- SQLite crash/restart recovery and event volume.

### 13.3 Manual acceptance checklist

- [ ] Windows 11 desktop: hidden startup, tray visible, correct shutdown.
- [ ] Two monitors, mixed DPI, focus handling, full-screen app behavior.
- [ ] Hotkey conflict with existing app surfaces actionable error.
- [ ] Microphone unplugged mid-recording and no audio permission.
- [ ] RU/UK/EN dictation, including Git repo and technical words.
- [ ] Airplane/offline mode: STT still works; Codex displays offline error.
- [ ] Token expires; agent does not silently switch provider.
- [ ] App Server crashes; UI remains responsive; mutating turn not auto-retried.
- [ ] Approval denied / timed out → action does not run.
- [x] Kill switch terminates a hung owned child in an automated test.
- [ ] CPU/RAM measured at idle and during transcription on target machine.

## 14. Definition of Done (MVP v0.1)

MVP v0.1 is done only when **all** of these hold:

1. A clean Windows 11 user can start the Avalonia app in tray mode without administrator privileges.
2. The global hotkey captures voice, locally transcribes RU/UK/EN, and shows an editable preview.
3. User can send typed/transcribed text to **actual Codex App Server** using a verified eligible ChatGPT subscription login and receive incremental output.
4. App displays real final status, task history, failure reasons and interrupt behavior.
5. GitHub or equivalent read-only MCP integration successfully retrieves useful data.
6. Default profile cannot execute unrestricted PowerShell or perform destructive/remote-write actions.
7. Approval requests are handled safely and denial is respected, with independent enforceable boundaries for any tools allowed to mutate state.
8. Crashes/timeouts/offline states do not lock UI or automatically replay side effects.
9. No secrets/audio leakage in defaults, logs, repository or generated artifacts.
10. Required tests pass and manual smoke test is documented with exact versions and machine conditions.

**Later than v0.1:** full workspace-write sandbox, sophisticated PowerShell execution, write-capable MCP tools, generalized application control, cross-platform support and fully autonomous workflows. Do not expand v0.1 scope until end-to-end voice → Codex → read-only tool flow works reliably.

## 15. Open decisions / implementation spikes

The coding agent must investigate and document these, rather than make unsupported assumptions:

1. Which installed Codex version and specific App Server methods/approval schemas are available on Windows?
2. Does an already signed-in Codex CLI session cover App Server inference under the user's subscription? If not, is app-specific Sign in with ChatGPT OAuth actually accessible for this client?
3. What sandbox policy is effectively enforced by the selected Codex version on Windows? Is native Windows sandbox support sufficient, or is an isolated VM/WSL-based execution environment necessary?
4. Can tool calls to the chosen MCP servers be intercepted before side effects? Otherwise, what credential/tool segregation enforces policy?
5. Which Whisper model offers an acceptable RU/UK/EN accuracy and latency on the target laptop? Benchmark, don't assume.
6. Does Avalonia's application lifecycle safely preserve `TrayIcon` while all windows are closed on the target OS? Test.
7. Will a dedicated Codex configuration profile be supported without interfering with the user's normal Codex CLI configuration? Confirm before editing settings.
8. What is the smallest correct packaging strategy for native Whisper libraries on Windows x64?

## 16. Development workflow and reporting

- Work in short feature branches or focused commits, with tests per milestone.
- After every milestone, report: implemented items, actual commands/tests run, passing/failing results, blockers, deviations from plan, and next milestone.
- When a security feature is unsupported, label it **unsupported** in UI/docs; never represent a prompt-only guard as OS-enforced safety.
- Keep tests offline whenever possible; live Codex tests are opt-in and may consume plan usage.
- Prefer explicit, comprehensible C# over complex frameworks and magic abstractions; no unnecessary ASP.NET server or distributed services.
- Do not hardcode private domains, repository names, credentials or personal file paths. Use example values and settings.

## 17. Official references (verify latest before implementation)

- Codex App Server protocol: https://learn.chatgpt.com/docs/app-server
- Sign in with ChatGPT / Codex App Server: https://developers.openai.com/siwc/token-sharing-open-source/codex-app-server
- Codex open source App Server: https://github.com/openai/codex/tree/main/codex-rs/app-server
- Codex platform/harness background: https://developers.openai.com/blog/codex-as-a-platform
- OpenAI Developer Docs MCP: https://developers.openai.com/mcp
- Avalonia TrayIcon: https://docs.avaloniaui.net/controls/navigation/trayicon/
- Microsoft Win32 RegisterHotKey: https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-registerhotkey
- Windows Credential Manager / DPAPI and Windows process isolation: consult current Microsoft docs during implementation.

---

## 18. First execution prompt to the coding agent

> Implement VoiceAgent incrementally following `plan.md`. Start by inspecting the repo/environment and performing milestone **M0**; write down confirmed Codex/App Server protocol, auth path, version and Windows sandbox constraints. Then implement **M1** and **M2** as a working end-to-end vertical slice with tests. Use .NET 10 + Avalonia UI only; do not use WPF/WinForms/Electron, a hosted backend, or a paid API-key fallback. Preserve default read-only permissions and do not execute destructive or remote-write actions. For any feature whose real authorization or sandbox support cannot be verified, stop that feature and report the exact blocker rather than guessing. Continue sequentially through the milestones when gates pass; after each milestone run builds/tests and update the checklist and project docs.

Discovery verification: the local Codex App Server returned 21 entries with the vscode source filter. Active IDE focus and live submission to an IDE chat remain unverified.

Discussion window implementation: mandatory recipient prefixes, a three-reply limit and manual pause. Validate both live provider connections before declaring the multiplayer workflow verified.


Playback supports Windows system voices and OpenAI Audio. Voice recognition and its model downloads are independent of playback.

Trayvora saves chat selections independently for Codex and Claude, applies playback and shortcut changes automatically, and commits API keys on Enter or when leaving the field.

**Portable data location (2026-11-15, owner decision):** The portable build keeps all of its data next to Trayvora.exe on whatever drive it is unpacked to, including C: (for example a Downloads folder on a single-drive computer). The earlier "outside C:" checks for settings, history, sessions, the Codex profile, models and the Claude and Codex Desktop profiles were removed. Local workspace accepts local folders on any drive and still rejects network shares, device paths and links. Revqora remains restricted to E:. Tests: AnyDriveTests writes settings, sessions and history under drive C:; the portable zip was unpacked to a folder on C: and started.
