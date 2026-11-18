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
  - an activity log of shortcut presses.
- **Settings:**
  - shortcuts;
  - speech engine;
  - voices;
  - key phrases;
  - theme;
  - ChatGPT and Claude sign-in.

## Build and run

You need the .NET SDK pinned in [global.json](global.json) on Windows.

```powershell
dotnet build
dotnet test
dotnet run --project src/VoiceAgent.Desktop
```

Or start `Trayvora.exe` from a published build. It creates its own data folders and finds Codex and Claude Code by itself.

## Layout

| Folder | Contents |
|---|---|
| `src/VoiceAgent.Desktop` | Avalonia UI, tray, shortcuts, Sessions |
| `src/VoiceAgent.Codex` | Codex App Server client |
| `src/VoiceAgent.Speech` | Whisper and OpenAI speech, audio capture |
| `src/VoiceAgent.Application`, `src/VoiceAgent.Infrastructure` | Core logic and storage |
| `tests/VoiceAgent.UnitTests` | Tests; live tests run only when their environment variables are set |
