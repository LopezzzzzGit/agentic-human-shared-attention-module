# Agentic/Human Shared Attention

ASHA gives a computer-use agent and its human operator a shared visual
language: the agent can mark a target before acting, and a human can teach a
workflow that is compiled into a semantic recipe. It is intentionally
independent of Lantern and any individual model provider: a local tool-calling
model belongs in the host harness, while ASHA supplies the desktop capability.

The implementation follows the build order in [SPEC.md](SPEC.md). The current
first milestone is the dependency-free TypeScript mark engine and the `asha`
command-line interface.

The intended product lifecycle, memory hierarchy, retention choices, and
delivery order are maintained in [ROADMAP.md](ROADMAP.md).

## Mark engine

With `cua-driver` installed, run the live overlay demo:

```powershell
node demo/marks-demo.ts
```

The demo intentionally holds its marks for 60 seconds. Add `--quick` while
developing. The daemon is started automatically when needed and marks are
removed by `clear()`.

The CLI keeps state in `%LOCALAPPDATA%\asha\marks.json`, allowing marks to be
created and acknowledged in separate commands:

```powershell
node bin/asha.mjs mark '{"kind":"circle","x":920,"y":540,"label":"Save"}'
node bin/asha.mjs ack mark-1
node bin/asha.mjs clear
```

`stdout` is JSON only. Driver diagnostics and errors go to `stderr`.

## Projects, sessions, and durable teaching memory

ASHA's saved unit is a **semantic teaching session**, not a stream of raw
audio or every pointer sample. Sessions live inside ASHA projects and can link
back to the conversation that interpreted them—Codex Remote today, another
harness later. This makes a later question such as “How did you teach me the
mailboxes?” answerable without coupling ASHA to one agent runtime.

```powershell
node bin/asha.mjs project create "Outlook coaching" --id outlook
node bin/asha.mjs session start --project outlook --title "Show the mailbox list" --harness codex-remote --harness-session <codex-thread-id>
node bin/asha.mjs session record <asha-session-id> '{"actor":"human","type":"mark","intent":"Identify mailboxes","target":{"app":"Outlook","label":"Mailboxes"}}'
node bin/asha.mjs session find mailboxes --project outlook
```

The ledger is stored under `%LOCALAPPDATA%\asha\ledger`. A harness link is a
reference to its own transcript/session; ASHA does not duplicate the audio or
whole chat history.

## Host integration

The future agentic harness can use Gemma QAT or any other local model capable
of tool calls. Ask ASHA for its provider-neutral function schemas with:

```powershell
node bin/asha.mjs manifest
```

Then map the result into the harness's native tool format and dispatch the
normalized calls to ASHA. [docs/harness-contract.md](docs/harness-contract.md)
defines the boundary; no Lantern dependency is required for the demonstrator.

## Teach recorder

The recorder is a separate .NET 8 Windows console application. It writes JSON
Lines to stdout and reserves stderr for diagnostics. Build it once, then run
it with F8 for pointing mode, F9 for demonstration mode, and Esc to stop:

```powershell
npm run build:recorder
& .\teach-recorder\bin\Release\net8.0-windows\asha-teach-recorder.exe
```

It uses global low-level Windows hooks and UI Automation. Text is batched by
focused control; password controls emit `redacted` and never their keys.

## Recipe compiler

For the stand-alone demonstrator, `asha compile` uses a deterministic local
compiler. It proves the JSONL-to-semantic-recipe flow without requiring a cloud
key or binding the demo to one model provider:

```powershell
node bin/asha.mjs compile fixtures/demo-notepad.jsonl --title "Save meeting notes"
```

The output is validated against the neutral recipe schema. Your harness can
replace this fallback with a local model adapter using the request object from
`src/recipe-compiler.ts` and the instruction in `prompts/compile.md`; validate
the model output with `assertRecipe` before replaying it.

## ASHA live presence and playground

The timed marks demo is only a smoke test. For a live visual presence and its
engineering playground, build and launch ASHA:

```powershell
npm run build:live
& .\interactive\bin\Release\net8.0-windows\asha-live.exe
```

The normal surface is a small, borderless blue-and-white **conversation orb**.
Only its circular surface receives pointer input; the transparent area around
it passes through to the desktop. Drag the orb itself to move it. Double-click
or right-click it to open a translucent controls panel; **Ctrl+Alt+A** toggles
that panel anywhere.

Its cloud-and-glass renderer receives real state and energy from the existing
Codex Remote WebSocket at `wss://127.0.0.1:9555/ws`. Start the usual Codex
Remote page, refresh it after an update, then use Whisper or server TTS: the
orb reacts to the live microphone/TTS meter. This bridge transports only
short-lived presence data; completed demonstrations belong in the ASHA ledger.

Expand **Playground and diagnostics** only when testing cues. Pick a cue style,
move the real mouse over any desktop target, and press **Ctrl+Alt+M** to leave
a persistent mark at that exact location. The panel lists active cues and can
clear them. Select an existing cue, then move the cursor to a new location and
press **Ctrl+Alt+Shift+M** to move it.

These marks are virtual, click-through overlays: they never move or click the
person's physical mouse. The future model uses the same CLI and tool manifest
to show an attention cue before asking a clarification or requesting a real
action. See [the interaction model](docs/interaction-model.md) for the
voice-and-desktop architecture and the physical-control consent boundary.

On Windows, ASHA renders its marks with a native, click-through WPF overlay.
This avoids relying on the driver cursor overlay for static shapes while the
same `asha` CLI stays available to the future harness.

### First live voice conversation

The current orb can run its own local voice loop without Codex Remote: it
records a short push-to-talk turn, sends it to the local Whisper/Kokoro service
at `127.0.0.1:9010`, asks a Groq model for the reply, then plays the local TTS
response.

Run `configure-groq.bat` once and paste your Groq API key when prompted. The
key is stored only as the Windows user environment variable
`ASHA_GROQ_API_KEY`; it is not written into this repository. Then start
`start-asha.bat`. Tap the orb once to begin listening, then tap it again when
you finish your sentence. Drag the orb instead of tapping to move it.

## Development

Node 22.6+ runs the dependency-free TypeScript source through native type
stripping. No `npm install` is required.

```powershell
npm test
```
