# Trayvora for Windows

Trayvora runs in the Windows notification area and transcribes speech locally. This build includes microphone capture and an editable transcript.

## Run the local speech build

With the .NET 10 SDK installed, run these commands in PowerShell. The source stays in Trayvora and the development data stays in its sibling Trayvora-Test directory.

```powershell
Set-Location 'E:\Temp\polymarket-hl-strategy\Trayvora'
$testRoot = 'E:\Temp\polymarket-hl-strategy\Trayvora-Test'
$env:TEMP = Join-Path $testRoot 'tmp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $testRoot 'dotnet'
$env:NUGET_PACKAGES = Join-Path $testRoot 'nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $testRoot 'nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $testRoot 'nuget-plugins'
$env:APPDATA = Join-Path $testRoot 'appdata'
$env:LOCALAPPDATA = Join-Path $testRoot 'localappdata'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:VOICEAGENT_MODEL_DIR = Join-Path $testRoot 'models'
$env:VOICEAGENT_SETTINGS_DIR = Join-Path $testRoot 'settings'
$env:VOICEAGENT_CODEX_DIR = Join-Path $testRoot 'codex-data'
$env:VOICEAGENT_HISTORY_DIR = Join-Path $testRoot 'history'
$artifacts = Join-Path $testRoot 'artifacts'
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
dotnet build --artifacts-path $artifacts
dotnet test --no-build --artifacts-path $artifacts --results-directory (Join-Path $testRoot 'results')
dotnet run --no-build --artifacts-path $artifacts --project src/VoiceAgent.Desktop
```

Start Trayvora by double-clicking **Trayvora.exe** in the application folder; it sets up its own data folders and finds Codex itself, so no launcher scripts are needed. Click the tray icon or choose **Open Trayvora**. The tray menu has **Open Trayvora**, **Sessions**, **Record / Stop**, **Settings…**, **More** (History…, Local workspace…, Show floating agent, Accounts (ChatGPT, Claude)…, Start with Windows) and **Exit**; **Stop everything** appears only while a recording or request is running. Select a microphone and choose **Auto**, **Русский**, **Українська** or **English**. If the model is missing, click **Download Whisper Base (148 MB)**. The app verifies its SHA-256. Click **Record** or press **Ctrl+Shift+Z** to start and again to stop. Recordings stop automatically at 2 minutes. Review and edit the result in **Transcript**. **Escape** cancels active work or hides the idle window. **Exit** closes VoiceAgent.

Right-click the tray icon and choose **Settings…** to change the microphone shortcut. Choose Ctrl or Alt, optionally add Shift, pick A–Z or Space, and select **Apply shortcut**. The shortcut changes immediately and is restored at the next launch. If Windows rejects a combination, the previous shortcut stays active. Windows uses Win+Z for Snap layouts. The legacy VOICEAGENT_HOTKEY environment variable is read only when no setting has been saved.

Audio is processed in memory. Type a message or edit the transcript, then choose **Send to Codex**. The reply appears as it arrives. Use **Cancel** to request an interruption and wait for the final status. Closing the overlay hides it while the request runs. See the [plan](plan.md), [architecture](docs/architecture.md), [App Server findings](docs/protocol.md) and [security boundaries](docs/security.md).

## Git and Revqora

Keep Revqora running with MCP enabled. Before starting Trayvora, set VOICEAGENT_REVQORA_EXE to the installed revqora.exe on E:. Automatic routing sends recognized questions about repositories, branches, commits and pull requests through Revqora. Choose **Revqora MCP** explicitly for ambiguous requests. Revqora requests and regular Codex messages share the selected chat. The Revqora route exposes read-only tools. If Codex requests a command, file write, broader permissions or an MCP confirmation, the read-only profile denies it and records the action category in History.

Use **More → Local workspace…** from the tray to select a local folder on any drive, open it in File Explorer, inspect the local Git branch and HEAD, or initialize a new repository in an empty folder. **Direct Git** in the request window reads only branch and HEAD in that selected folder. It runs only on explicit selection and never as a fallback from MCP.

While a request runs, use **Stop everything** in the tray to terminate the owned App Server process. If a turn has no terminal response, History records an unknown outcome.

## Voice shortcuts and recognition

**Ctrl+Alt+Shift+W** (Settings → Shortcuts → Computer sound) records what plays on the Windows default playback device through WASAPI loopback. Press again to stop: the speech is recognized (OpenAI by default when a key is saved; Settings → Speech recognition → Computer-sound shortcut uses), sent to OpenAI as a quick question, and the answer is read aloud. Nothing appears on screen while it works (safe during screen sharing); the tray icon tooltip shows the state and any problem. Every active output device (speakers, headset) is recorded and the one that carried the sound is used. **Ctrl+Alt+Shift+S** stops reading.

Settings → **OpenAI API key** holds the one key used by OpenAI recognition, Quick and Deep questions, the computer-sound shortcut, OpenAI voices and Text to audio; after saving, the field is cleared and the last characters of the key are shown. Sessions (next to Voice mode) and the Questions modes (next to New conversation) each have a recognizer chooser: Automatic, Local Whisper or OpenAI.

Hold **Alt** and scroll vertically to adjust Trayvora speech playback in 5-point steps. The control works with Trayvora in the tray and saves the playback level.

Choose **Questions · GPT-6 Luna** for general questions via the OpenAI Responses API. Type a question or use the regular recording shortcuts, then choose **Ask**. Background recording submits to the active mode. Short questions keep six exchanges until restart. Long questions save the full transcript under **Memory** beside the executable and summarize older turns through the API. **New conversation** archives the previous long transcript in that folder. The default shortcuts are **Ctrl+Alt+Shift+L** for long questions and **Ctrl+Alt+Shift+Q** for short questions. Press twice to record and submit. Questions reuse the saved recognition key or separate playback key, use no tools, and are billed separately. **Read replies aloud** applies here too.

For a chat visible in both applications, expand **Choose Codex Desktop chat**. The chat list loads when you first expand it. Filter it by title, select a chat; the selection is saved automatically. Use **Refresh chats** after creating a new conversation. The main window shows its title under **Active chat**. The regular send button and the voice preview and background shortcuts use that destination until you choose **Switch to Trayvora chats**. **New in Codex** opens a new chat in Desktop. Send its first message there and refresh the list. Use **Ctrl+Alt+Shift+G** twice to record and submit a voice message to the selected chat, or type in **Transcript** and click **Send to Desktop** in the main window. This leaves turn execution and its permissions with Codex Desktop. **Read history** loads the latest 20 turns. **Read replies aloud** uses installed Windows voices for answers to requests sent from Trayvora. Keep the selected chat open in Codex Desktop during the first test. An unloaded chat can retain queued requests until it is opened. The shared chat profile path is configurable. The **Codex response** field is editable while idle. **Read text** reads its contents. **Test sound** uses the field or a built-in phrase if empty. **Playback settings** lets you select a Windows voice and speed or use OpenAI Audio with the saved recognition key or a separate playback key. A shared key follows changes in Speech recognition. OpenAI mode sends the playback text to the Audio API and is billed separately. **Cancel** stops Trayvora waiting. Once queued, manage the message or turn in Codex Desktop to prevent duplicate submissions.

**Dictate into Codex Desktop** uses a separate shortcut, normally **Ctrl+Alt+Shift+D**. Open the desired Codex chat and focus the message field. Press the shortcut to start, then press it again to stop. Trayvora transcribes with the selected speech provider and inserts text into the focused Codex window for you to review and send. If focus changes, Trayvora offers the draft with **Copy text**. This shortcut does not submit a turn through Trayvora. Change it under **Settings → Shortcuts**.

Use the **Record and show transcript** shortcut once to start and again to stop. A panel at the top of the current monitor shows the transcription status, then lets you edit and send the draft. **More → Show floating agent** in the tray displays the speaker button. Click it to record with automatic submission. The icon indicates recording, transcription and sending.

Open **Settings…** from the tray to change all three shortcuts. Default shortcuts:

- **Ctrl+Shift+Z** starts and stops recording, then opens a compact editable preview.
- **Ctrl+Alt+Shift+X** sends the current preview.
- **Ctrl+Alt+Shift+Z** starts and stops background recording and sends the resulting text automatically.
- **Ctrl+Alt+Shift+A** is Dictate anywhere: press, speak, press again. In any app (Notepad, Word, a browser, a messenger) the text is typed where the text cursor blinks; with no cursor it is copied to the clipboard for Ctrl+V. No window appears and nothing is sent anywhere. Apps that draw their own cursor without a Windows caret (some browsers and Electron apps) get the clipboard instead.
- **Ctrl+Alt+Shift+S** stops any reply being read aloud at once, whether the Windows voice or an OpenAI voice reads it (Text to audio playback too). Change it in Settings → Shortcuts → Stop reading aloud.

**Show floating agent** places a speaker button on the desktop. Click it to start recording and again to stop and send. The red button indicates recording. The button is disabled while processing. Drag it using the right mouse button.

In **Speech recognition**, select **Local Whisper** or **OpenAI Audio API** and apply the settings. Local Base is the default. Small, Medium and Large v3 Turbo can be downloaded from the same panel. Model files are verified with SHA-256. Cloud defaults to gpt-transcribe and uses a separate API key and billing. Windows protects the saved key for the current user. Forget API key switches back to local recognition. Settings changes apply to the next recording.

With **Local Whisper** selected, edit **Whisper hints**; the changes save when you leave the field. Enter one term per line, such as `Revqora`, a repository name, or a phrase like `создать бранчу`. The editable starter list includes common Git phrases. Clear the field and leave it to disable hints. The list is passed to the local recognizer as an initial prompt.

## Accounts

The **Accounts** section at the bottom of the main window sidebar shows whether **ChatGPT** and **Claude** are signed in; Trayvora checks both at start. Click either to open the Accounts page (also **More → Accounts (ChatGPT, Claude)…** in the tray). **Sign in** runs the official sign-in of the tool for you: `codex login` for Trayvora's own Codex profile (stored in codex-data next to Trayvora.exe) and `claude auth login --claudeai` for Claude Code. The browser opens; if it does not, use **Open sign-in page** or **Copy link**. If the browser shows a code, paste it into the field and choose **Send code**. The page then shows the account (for Claude: email, plan and when the sign-in expires). **Sign out** runs the tool's logout. Tokens stay in the tools' own profiles; Trayvora does not read or store them.

## Main window, Get started and templates

The main window shows each mode and chat as a conversation: your messages on the right, replies on the left, rendered as Markdown (headings, lists, tables, code blocks with a copy button). Hover a reply to copy it or read it aloud. Each mode and chat keeps its own conversation while the app runs; a Trayvora Codex chat shows its last 15 saved requests when you open it. Codex replies appear while they are written; Claude Code replies appear when finished.

**Get started** (sidebar, bottom) checks the microphone, the speech model, the ChatGPT and Claude sign-ins and the optional OpenAI API key, with a button to fix each item, and lists every shortcut and the session key phrases. It opens by itself the first time Trayvora starts in a folder. **Settings** in the sidebar opens the Settings window.

The template button (next to Record, and next to the microphone in Sessions) inserts a saved message or saves the current text as a template (settings\templates.json, up to 40).

In **Sessions**, the search box filters sessions by title and message text, **Export** saves the open session as Markdown, and **Voice mode** sends a dictated message as soon as it is transcribed and reads replies addressed to you aloud. When a session finishes while Trayvora is not the active window, a Windows notification appears.
## Audio to text and text to audio

Two tools in the main window sidebar under **Tools** open as full pages with **← Back**.

**Audio to text** turns a recording into text. Choose an MP3, WAV, M4A, AAC, WMA, FLAC or other file Windows can play, pick the language and choose **Transcribe**. The file is read in parts of about two minutes, each cut at the quietest moment so words are not split; memory use does not grow with the length. Choose the engine on the page: **Local Whisper** (runs on this computer and loads its model once per file) or **OpenAI** (the audio is sent to the OpenAI Audio API and billed separately). The page starts with the engine chosen in **Settings → Speech recognition**; switching here does not change Settings. With Local Whisper, pick the **Whisper model** on the page (Base, Small, Medium or Large v3 Turbo). A model that is not on disk can be downloaded right there; it is verified with SHA-256 before use. Bigger models are more accurate and slower; Large v3 Turbo gives the best accuracy. The choice applies to this page only. The OpenAI model and API key come from Settings → Speech recognition, and the page says what is missing. The transcript appears part by part, can be edited, copied or saved as .txt, and **Cancel** keeps the text recognized so far. **Add time marks between parts** prefixes each part with its start time.

Drop audio files onto **Audio to text** or pick several at once: they are transcribed one after another, each with its own status. **Save all…** writes one .txt per file into a folder; **Save as .srt** writes subtitles (times are approximate within each two-minute part).

**Settings → Key phrases** lists the phrases Trayvora looks for in Sessions messages, one per line: phrases that forward the reply to the other agent (`перешли Клоду`) and phrases that start a discussion (`обсуди с Кодексом`). Edit the lines or add your own; each line must name Claude/Клод… or Codex/Кодекс… and is found anywhere in a message, ignoring case and extra spaces. A joining word before it (`и`, `потом`, `and then`) is removed with it. **Also catch built-in variations** keeps the wider built-in recognition (other word forms, `→ Claude`, `#codex`); turn it off to use only your lines. Saved in settings\key-phrases.json.

**Settings → Theme** sets font, size and colour for chat text, the text you type, and the interface and menus (settings\appearance.json).

**Activity** (Tools in the sidebar) lists the last 30 shortcut presses and what came of each: recording started, length and loudest level, recognized or no speech, OpenAI request sent, answer or error. Only times, counts and errors are kept (no transcripts or answers), in settings\logs\activity.json; older entries are dropped. The computer-sound shortcut records what plays in the speakers or headphones, not the microphone, so speaking into the microphone alone shows up there as silence.

**Text to audio** speaks text with an installed Windows voice (on this computer) or an OpenAI voice (uses the key from Speech recognition or Playback settings, billed separately). It starts from the voice and speed chosen in Playback settings. Long text is generated in parts and joined into one file. **Play** listens in Trayvora; **Save as…** writes MP3 or WAV. Automatic voice selection needs an installed Windows voice for the text language.

## Open a past response

Choose **History** in the main window sidebar or **More → History…** in the tray and select a request. The list shows its time and status. The detail shows the original submitted text, tool activity and the saved response, including any partial response received before cancellation or disconnect. Abandoned running requests appear as **Unknown** after restart. Trayvora keeps the most recent 200 finalized entries per chat in a local, unencrypted SQLite database. **Delete selected entry** removes the selected entry from Trayvora history. Codex manages its own conversation history in the dedicated profile. Use **New chat** to start a separate conversation, or select a saved chat to continue it across app launches. The route controls tool availability for the next message. History opens for the current chat. **Show requests from all chats** also includes rows from before chat grouping was introduced. The previous Codex and Revqora threads can each be continued. Recent direct Git results are included as context in subsequent Codex requests in that chat.

Windows can show a generic completion notification when the request window is in the background. Windows notification settings control whether it appears. Sleep requests cancellation of active work. Resume refreshes the microphone list and leaves interrupted requests in History.

## Sign in to the Trayvora Codex profile

From the PowerShell session above, sign in once using the dedicated Codex home. The profile and conversation history live next to Trayvora.exe. Use the ChatGPT sign-in option.

```powershell
$env:CODEX_HOME = $env:VOICEAGENT_CODEX_DIR
$env:CODEX_SQLITE_HOME = $env:CODEX_HOME
New-Item -ItemType Directory -Force $env:CODEX_HOME | Out-Null
codex login
codex login status
```

If the application cannot find Codex, set `VOICEAGENT_CODEX_EXE` to the full installed path of `codex.exe`. Trayvora checks for ChatGPT-managed auth, disables tool integrations in this profile and resumes its saved thread across launches. If Codex no longer has the saved thread, stop Trayvora and rename the corresponding thread ID file inside the Codex directory to start a new thread.

For the optional speech tests, set `VOICEAGENT_SPEECH_FIXTURES` to a directory on E: containing the pinned `ggml-base.bin` and 16 kHz, mono, 16-bit PCM WAV files named `en.wav`, `ru.wav` and `uk.wav`. The supplied test checks a short known fragment from each fixture. The English and Russian fixtures use local Windows SAPI speech. The Ukrainian fixture uses [FLEURS](https://huggingface.co/datasets/google/fleurs) `uk_ua` validation row 0, converted from float WAV to PCM.



In Claude Code mode, enter the native executable path and existing profile path, find sessions, filter by title, and select one. Sending runs Claude CLI with the selected session ID and default permissions; the reply appears while it is written. Session lists show the most recently used chats first. Close the interactive copy of that session before sending. Automatic IDE focus tracking is not available.

The portable launcher discovers Codex through PATH, the registered Desktop package or the installed VS Code extension. Claude Code uses PATH or the native user installation unless an executable is selected manually. VS Code sessions in the same local Codex profile are discoverable through thread/list. Remote or WSL sessions may use a different profile.

Open **Discussion** from the sidebar of the main window. Sessions open inside the same window; **← Back** (or Escape when nothing is running) returns to the main view. The view lists saved **sessions**, newest first. A session is a viewer of everything sent between you, Codex and Claude. **New session** creates one; typing in an empty window also creates one. The first message becomes its title; click the title to rename it. Every message is saved as JSON under `discussions` in the settings folder and stays after restarts until you choose **Delete** and confirm. Deleting removes only Trayvora's copy; the linked Codex and Claude chats are kept.

Each session has its own bindings, and the agents always answer in those chats:

- **Codex** defaults to a dedicated Trayvora thread for that session. You can pick one of your Trayvora Codex chats, or press **Load Desktop chats** and pick a Codex Desktop chat. Desktop turns run with Codex Desktop's own permissions; a reply that is only queued appears in Codex Desktop.
- **Claude** defaults to the session selected under **Claude Code** in the main window; **Load Claude chats** lists the others. Claude resumes that chat with its own tools, MCP servers and permission settings, exactly like the Claude Code mode.

Changing a binding in one session does not affect other sessions or the main window. Each agent keeps its own context: it receives only the message addressed to it. Your message is sent exactly as typed; a reply passed on from the other agent starts with a short label such as "Codex wrote:". Only "discuss with" requests add instructions. Trayvora does not add other messages from the session. Hover a message to show its toolbar, or right-click it: **Copy**, **Forward to Codex/Claude** (only to an agent that has not received it) and **Delete from this session**. Deleting removes the message from Trayvora's saved session only; Codex and Claude keep their own chat history. The chevron in the session header folds the chat settings into one summary line for more room. Several sessions can run at the same time; Trayvora Codex turns still run one at a time in the shared Trayvora profile.

Pick the recipient with the **To** chips: **Codex** (default), **Claude**, or **Both**. A leading `Codex:` or `Claude:` overrides the chips. Press Enter to send and Shift+Enter for a new line. Click the **microphone** beside the send button to dictate: it records from the microphone and language chosen in the main window, click it again to stop (Escape cancels), and the transcript is inserted into the editor for review before you send. Dictation waits if Trayvora is already recording or running a main-window request. The **⇄** toggle joined to the **Codex** and **Claude** chips passes that agent's reply on to the other agent (and shows it here). Instead of the toggle you can put a key phrase anywhere in a message to Codex or Claude; Trayvora recognizes it itself (typed or dictated), removes it from what the agent receives, and passes the reply on to the other agent. The message shows **⇄ then Claude/Codex**. Examples: «перешли Клоду», «передай ответ Кодексу», «потом отправь Claude», «перешли відповідь Клоду», "forward to Claude", "then send it to Codex", "→ Claude", "#codex". The **?** next to the chips lists them. Names are recognized as Claude/Клод/Клоду and Codex/Кодекс/Кодексу; the phrase can stand anywhere in the message and must name the other agent; without such a phrase the reply comes only to you. **Discuss with the other agent.** Put a phrase such as «обсуди с Клодом», «обсуди с Кодексом решение», «посоветуйся с Клодом», «согласуй с Кодексом», «обговори з Клодом», "discuss with Claude" or "consult Codex" anywhere in a message to Codex or Claude. The agent you wrote to leads: it first does its own part (looks at the code or facts), sends the other agent its findings, a plan and questions, gets the answer back, and then decides itself whether the plan is ready or the work is done correctly. If it is, it sends you the final decision (marked **✓ final answer**) and stops writing to the partner; if not, it asks the partner again. The partner's replies always go back to the lead. Trayvora allows at most five exchanges by default; change this under **Settings → Sessions → Discussion rounds** (1–10, applies to the next discussion); on the last turn the lead is told to send you the final decision, and that reply always goes to you. Your message shows **⇆ discuss with Claude/Codex**. With the toggle off the agent is told to answer only you; it hands off only when your message explicitly asks it to (for example a dictated "then send it to Codex"), by starting its reply with the other agent's name. If an agent cannot answer (for example Claude Code is missing or signed out), the session shows and saves a red "did not answer" notice with the reason, and Trayvora still delivers the remaining messages of the round. Trayvora finds `claude.exe` from the path set under **Claude Code**, then PATH, `~\.local\bin`, and finally the copy bundled with Claude Desktop (newest version). Unexpected interface errors are written to `settings\logs\errors.log` instead of closing the app. Each round permits up to four agent replies. Replies appear while the agent writes them, with what it is doing (for example "Read · Client.cs") and the elapsed time; a message to **Both** reaches Codex and Claude at the same time. Agent replies are shown as formatted Markdown: headings, lists, tables, quotes, web links and code blocks with **Copy**; your own messages are shown exactly as typed. A hand-off may also be written as `**Claude:**` or `@Claude:`. Messages can be up to 12,000 characters; a longer reply passed to the other agent is cut with a note. If Claude Code was not allowed to use a tool, the reply shows which ones. The view follows new text only while you are at the bottom; otherwise **↓ New messages** jumps there. **Pause** (or Escape) stops the visible session and keeps what each agent had written, marked **⏸ interrupted**. Long sessions open with the latest 60 messages; **Show earlier messages** loads older ones in steps of 60.

The main window uses a sidebar for modes (Codex, Quick questions, Deep questions, Claude Code) and workspace windows (Discussion, History). The composer at the bottom holds the transcript, **Record**, **Cancel** and the send button; Ctrl+Enter sends.
