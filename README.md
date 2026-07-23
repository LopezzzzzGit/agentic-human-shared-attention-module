# Agentic/Human Shared Attention

ASHA gives a computer-use agent and its human operator a shared visual
language: the agent can mark a target before acting, and a human can teach a
workflow that is compiled into a semantic recipe. It is intentionally
independent of Lantern and any individual model provider: a local tool-calling
model belongs in the host harness, while ASHA supplies the desktop capability.

The implementation follows the build order in [SPEC.md](SPEC.md). The current
Windows demonstrator combines the dependency-free mark engine with the ASHA
orb, local speech, permission-gated vision, retained sessions, teaching
telemetry, and visible computer control.

The intended product lifecycle, memory hierarchy, retention choices, and
delivery order are maintained in [ROADMAP.md](ROADMAP.md). The short
competition setup and test sequence is in
[docs/hackathon-demonstrator.md](docs/hackathon-demonstrator.md).

## Essential development documents

Anyone developing ASHA should read these documents as one maintained set:

1. [SPEC.md](SPEC.md) — the original module contracts and measured driver
   foundation.
2. [Interaction model](docs/interaction-model.md) — ASHA's human-facing
   product and interaction principles.
3. [Product roadmap](ROADMAP.md) — lifecycle, memory architecture, delivery
   order, and near-term acceptance criteria.
4. [Design-note register](docs/design-notes/README.md) — permanent, numbered
   proposals and implementation decisions.
5. [Harness contract](docs/harness-contract.md) — the model-neutral boundary
   between ASHA and external agent runtimes.

Numbered design notes are actionable unless their status explicitly says
otherwise. Their identifiers are permanent: implemented, rejected, deferred,
or superseded notes remain in the register and are never renumbered or reused.

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

An agentic harness can use Gemma QAT or any other local model capable
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
person's physical mouse. The live ASHA runtime uses the same visual language
before guidance or a separately permitted physical action. See
[the interaction model](docs/interaction-model.md) for the
voice-and-desktop architecture and the physical-control consent boundary.

### Permission-gated computer control

Computer control has two distinct gates. In **Settings → Computer control**,
explicitly allow the maximum capabilities ASHA may use. Every capability is
off by default. Then start a retained session and press **Enable computer
control** to create a temporary lease. Ending the lease immediately removes
active control without changing the global limits.

Depending on the allowed policy and active lease, the voice model can:

- open or activate any application returned by Windows' installed Start-app
  catalog, using its ordinary display name rather than an executable path;
- open an existing non-system folder in Explorer without modifying it;
- move the physical pointer, click, double-click, right-click, drag, or scroll
  only when physical-cursor use is separately allowed;
- type short non-sensitive text and press approved navigation keys only when
  keyboard interaction is separately allowed;
- request a fresh pointer-area, foreground-window, entire-desktop, left-screen,
  or right-screen view independently of the person's mouse position.

Application names are resolved dynamically; Outlook, LM Studio, DaVinci
Resolve, Photoshop, and other applications are not individual command
implementations. The launch tool never accepts a command line, executable
path, or URL. Folder opening blocks Windows, program, and application-data
locations. Physical pointer actions require a recent coordinate-mapped view,
validate the currently exposed top-layer surface, remain visibly framed, and
are written to the active session ledger. Stop the control session to remove
the capability immediately.

The virtual-cursor policy, visibility, and demonstration settings are present
as a separate capability. Background interaction through the installed CUA
driver is the next integration slice. Until it is connected, ASHA never
silently substitutes the physical cursor for a permitted virtual action.

Groq is the demonstrator's swappable research provider. Keys remain outside
the repository and judges bring their own key. The provider boundary can be
replaced by a private local tool-calling model without changing ASHA's desktop
tools or session format.

On Windows, ASHA renders its marks with a native, click-through WPF overlay.
This avoids relying on the driver cursor overlay for static shapes while the
same `asha` CLI stays available to the future harness.

### First live voice conversation

The current orb can run its own local voice loop without Codex Remote: it
detects spoken turns through the local speech service at `127.0.0.1:9010`, asks
the configured Groq model for a reply, and plays local TTS. Tap the orb once to
enter free conversation; ASHA detects pauses and returns to listening after
each reply. Tap the centre again only when you want to end the conversation.

Bring your own free Groq API key. Run `configure-groq.bat` once, paste one key
or a comma-separated list, restart ASHA, then run `start-asha.bat`. Keys are
stored only in the Windows user environment and never in this repository.

For multiple accounts, ASHA resolves keys in this order:

1. `ASHA_GROQ_KEYS`, as a comma-separated user environment variable.
2. `%USERPROFILE%\.groq\keys.txt`, containing one comma-separated line.
3. `%USERPROFILE%\.lantern\keys.json`, using the string field
   `ASHA_GROQ_KEYS`.
4. The legacy single-key variable `ASHA_GROQ_API_KEY` for existing installs.

Successful keys are sticky. HTTP 429 responses rotate immediately to the next
key, rate-limited keys cool down according to Groq response headers, and
structural HTTP errors fail fast. The default vision model is configured with
`ASHA_GROQ_MODEL=qwen/qwen3.6-27b`; ASHA disables Qwen reasoning output and
strips any unexpected `<think>` block before it reaches chat, tools, or speech.

The checked-in `.env.example` contains placeholders only. ASHA does not need a
`.env` file inside the project: prefer the setup script or one of the external
key sources above. If no key is configured, the app still starts and explains
how to enable inference.

## Development

Node 22.6+ runs the dependency-free TypeScript source through native type
stripping. No `npm install` is required.

```powershell
npm test
```
