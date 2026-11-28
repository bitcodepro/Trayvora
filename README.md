# Trayvora

A Windows tray app for talking to coding agents by voice. Trayvora transcribes speech locally (Whisper) or through OpenAI and sends it to Codex or Claude Code. It shows the replies as they arrive and can read them aloud.

## What it does

- **Voice to agents.** Global shortcuts record the microphone and send the text to Codex, Codex Desktop or Claude Code. You can also send without reviewing first.
- **Sessions.** One chat with both Codex and Claude:
  - send a message to one or both agents;
  - forward a reply to the other agent;
  - let the two agents discuss a task.

  You can attach files and pictures to a message.
- **Questions.** Quick and deep questions to OpenAI. The **computer sound** shortcut records what plays in the speakers, sends the question and reads the answer aloud.
- **Dictate anywhere.** Types the recognized text at the cursor in any app.
- **Tools:**
  - audio to text (files of any length, can also save subtitles);
  - text to audio;
  - history of requests;
  - an activity log of shortcut presses;
  - an error log: every error of the last 7 days with where it happened and which chat or session.
- **Settings:**
  - shortcuts;
  - speech engine;
  - voices;
  - key phrases;
  - theme;
  - ChatGPT and Claude sign-in.

## Install on Windows

Download `trayvora-win-x64-setup.exe` from the [latest release](https://github.com/bitcodepro/Trayvora/releases/latest) and run it. Setup installs for your Windows account without administrator privileges. Open **Trayvora** from the Start menu, or add an optional desktop shortcut during setup.

The installation and its data are in `%LOCALAPPDATA%\Programs\Trayvora`. Quit Trayvora from the tray before uninstalling it from **Settings → Apps → Installed apps**. Uninstall keeps settings, history, the local Whisper model, and Trayvora's own Codex profile in that folder for a later reinstall.

For a portable copy, download `trayvora-win-x64.zip`, extract it to a writable folder, and run `Trayvora.exe`. Each copy keeps its own data beside the executable. Both installed and portable copies receive in-app updates from the release ZIP. `SHA256SUMS.txt` lists the checksums for both downloads.

## Interactive controls

- Expand **Work log** under a Codex or Claude reply to follow tool calls, their outcomes and background progress. Codex can also report plans, public commentary and reasoning summaries. Claude reports MCP permission requests. The log stays with the saved reply. Trayvora filters tool arguments, tool output and internal reasoning from this view.

- In **Sessions**, keep sending while the agents work. Each agent has its own queue. **Stop Codex** or **Stop Claude** stops just its current request. **Pause queue** holds waiting messages until you choose **Resume queue**. The message menu lets you edit or remove a queued message. **Stop session** stops both active requests, marks waiting messages **Not sent**, and keeps partial or finished replies. For messages known to be unsent, **Retry unsent message** restores an editable draft.
- Drafts are saved per session. The delivery badge clears when the agent accepts or starts replying. After an interrupted or uncertain Codex Desktop delivery, use **Check reply** to look for the response in the original chat, including its first message. **Clarify current task** sends the editor text to the active Codex turn when Trayvora has its turn ID.
- Replies come back to you by default. Use the relay toggle, **Discuss with**, or the forward action when you want to share a reply with the other agent. Voice mode sends automatically only while the selected destination and an empty composer are still in effect.

- In **Sessions**, select **Command Claude** or **Command Codex** next to **Voice mode**, type `/`, then click a suggestion or choose it with ↑ and ↓ and press Enter to insert it. Release Enter and press it again or click **Run command** to run it. Use `/mcp` to check servers in the selected Claude workspace, `/resume` to pick a saved chat by title, and `/effort` or `/model` to set the next Claude turns in this Trayvora session. `/help` lists the supported commands. Command results are saved in the conversation as **Claude CLI → You** or **Codex CLI → You**. Turn the mode off to return to your message draft, where slash-prefixed text follows the usual recipient and relay settings.
- In **Command Codex**, use /mcp to check the effective MCP configuration of a local Trayvora session or list configured servers in the selected Codex Desktop profile. Local sessions bound to **Previous Revqora chat** use the read-only Revqora MCP route. Other local Sessions disallow MCP servers. Use /status to check sign-in or /version to check the installed CLI. Open **Codex console** for full interactive commands such as /model, /resume, and live /mcp status. Codex starts in the workspace and profile for the room’s selected Codex route, using Trayvora’s own profile for local Codex chats.

- Open **Claude console** in **Command Claude**, or **Manage MCP in console** below an MCP result. The terminal runs interactive Claude Code with the configured profile and workspace. Type `/mcp` in the terminal, select the server and choose **Reconnect** or **Authenticate** when offered. Run the original room’s `/mcp` again to refresh its status or after switching Claude profiles. Switching a running console to another workspace asks for confirmation. Its processes stay active until the replacement window is ready.
- Trayvora checks the Claude Code account at startup and opens sign-in if the CLI reports that the session needs it. Claude uses the selected chat's workspace and CLI profile. When Claude asks permission to call an MCP tool, review its name, arguments and workspace, then choose **Allow once** or **Deny**.
- Enable **Settings → Theme → Plain text replies (continuous mouse selection)** to drag across paragraphs in an answer. The reply's **Copy** action includes link addresses and file paths in plain text. If streamed formatting makes a selection ambiguous, clearing the selection shows the latest text.

- Choose **Check for updates** in the tray or **Get started**. In the update window choose **Update** to download, verify and restart. **Cancel** closes the window during checking or downloading. A failed checksum leaves the installed copy running.
- Click a source link in either conversation feed to open **File preview**. Paths are resolved in the author's workspace. Select lines and choose **Ask Claude** or **Ask Codex** to prepare a draft. **Changes** shows this file's working-tree diff against Git HEAD. For older messages without a known workspace, choose the file manually.
- The Sessions microphone keeps the original room's draft when you switch rooms before transcription finishes. For a Desktop delivery that is still pending, use **Check reply** or **Open chat** on its message. Checking never resubmits it.
- For IDE routing, use **Export VS Code extension…** in the sidebar, then **Extensions: Install from VSIX** in VS Code. Enable **Follow active IDE session** in Trayvora. Preview and background recording use a recognized chat UUID from the focused editor. For a tab without a session URI, run **Trayvora: Pin Agent Chat to Current Tab**, choose the agent and enter the existing chat UUID. Trayvora recognizes the Claude Code editor and asks only for its chat UUID. Pins last for the lifetime of the tab. Outside VS Code the selected Trayvora route is used. Dedicated dictation and Q&A shortcuts keep their own destinations.

## Build and run

You need the .NET SDK pinned in [global.json](global.json) on Windows.

```powershell
dotnet build
dotnet test
dotnet run --project src/VoiceAgent.Desktop
```

Or start `Trayvora.exe` from a published build. It creates its own data folders and finds Codex and Claude Code by itself.

To build the release ZIP and installer, run `./scripts/ci/build.ps1 -OutputRoot artifacts/windows-package` from a full Git checkout with Inno Setup 6.7.4 available. Use a fresh output directory and pass `-InnoSetupCompiler` if `ISCC.exe` is installed outside its default location. GitHub Actions runs the installer smoke test on a fresh hosted Windows runner before the release job can publish either download.

## Layout

| Folder | Contents |
|---|---|
| `src/VoiceAgent.Desktop` | Avalonia UI, tray, shortcuts, Sessions |
| `src/VoiceAgent.Codex` | Codex App Server client |
| `src/VoiceAgent.Speech` | Whisper and OpenAI speech, audio capture |
| `src/VoiceAgent.Application`, `src/VoiceAgent.Infrastructure` | Core logic and storage |
| `tests/VoiceAgent.UnitTests` | Tests; live tests run only when their environment variables are set |
