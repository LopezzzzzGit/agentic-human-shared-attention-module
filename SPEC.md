# Agentic/Human Shared Attention — Build Spec v1

**For the Codex build sessions.** This document is your complete starting
knowledge: what to build, the measured facts about the driver you build on,
three module briefs in build order, and acceptance criteria. Everything here
was verified on the target machine (Windows 11, cua-driver 0.8.3) — trust it
over guesses.

---

## 1. What this is

Computer-use agents and humans lack a shared visual language. Agents click
blind; humans steer with paragraphs of text. This plugin adds the missing
deictic channel — *this, here, watch me* — on the live desktop:

- **Show Me** — the agent marks the element it resolved as its target
  (box / dot / label) BEFORE acting; after verification the mark confirms.
- **Look Here** (the gesture, namesake of the tagline) — the human points, clicks, or circles with their own
  mouse; the mark resolves to a semantic UI element and is handed to the
  agent as ground truth.
- **Watch Me** — the human demonstrates a workflow once (clicks,
  double-clicks, right-clicks, text, hotkeys, scroll, drag, waits). The
  demonstration is recorded WITH semantic context per event.
- **Recipe, not macro** — GPT-5.6 compiles the recorded demonstration into
  a semantic recipe: intent + multiple target addresses + expected outcome
  per step. Coordinates are evidence, never the learned procedure.

This repo contains the three core modules. A host harness (any agent
runtime) consumes them through the CLI contract in §5. The host and the
replay logic are NOT part of this repo.

## 2. The display/act layer you build on: cua-driver

An open-source background computer-use driver (trycua/cua, MIT) is already
installed and provides the screen overlay. Facts, all measured:

- Binary: `%LOCALAPPDATA%\Programs\Cua\cua-driver\bin\cua-driver.exe`
  (resolves via symlink to `~\.cua-driver\packages\current\`).
- The overlay needs a long-lived daemon: run `cua-driver serve` once
  (stop with `cua-driver stop`). One-shot CLI calls then reach it:
  `cua-driver call <tool> '<json-args>'`.
- **Agent cursors are instanced**: every distinct `cursor_id` is its own
  independent overlay sprite. `session` groups them; `end_session`
  removes a session's cursors.

### Tool schemas (measured, complete)

`move_cursor` — move an overlay instance. Does NOT move the real mouse.
```json
{ "x": 800, "y": 500, "cursor_id": "mark-1", "session": "asha" }
```

`set_agent_cursor_motion` — appearance + motion of one instance:
```
cursor_id            instance name (default 'default')
cursor_icon          'arrow' | 'teardrop' | PATH to PNG/JPEG/SVG/ICO ('' = default)
cursor_color         hex or CSS name
cursor_label         short text shown near the cursor
cursor_size          dot radius in points (default 16)
cursor_opacity       0.0–1.0 (default 0.85)
arc_size             path deflection [0,1], 0 = straight (default 0.25)
arc_flow             asymmetry [-1,1] (default 0)
spring               settle damping [0.3,1.0], 1.0 = no overshoot (default 0.72)
glide_duration_ms    [50,5000]; omit = speed-based
dwell_after_click_ms [0,5000] (default 80)
idle_hide_ms         [0,60000]; 0 = NEVER hide (default 20000!)
```

`set_agent_cursor_style` — `gradient_colors` (hex array tip→tail),
`bloom_color` (halo), `image_path` (same as cursor_icon).

`get_agent_cursor_state` — read back the current config. Use it to verify.

### THE ICON TRICK (this is how marks work without forking the driver)

A "cursor" with a custom transparent PNG/SVG icon IS a static mark. One
`cursor_id` per active mark: a circle sprite = a circle around a target, a
box sprite = a bounding box, a dot sprite = a point. `cursor_label` adds
text. `cursor_size` scales it. **Set `idle_hide_ms: 0`** or marks vanish
after 20 s. Repo ships `assets/circle.svg`, `assets/box.svg`,
`assets/dot.svg` as starting sprites — replace freely.

### Known limitation (do not fight it, design around it)

The overlay IS visible in screen captures (measured: driver screenshots AND
GDI CopyFromScreen). Consequence: **capture before you draw**, and never
OCR a frame that has your own marks on it. (A capture-exclusion driver
patch exists as a separate upstream PR — not this repo's problem.)

### Recording truth (measured — this is why module 2 exists)

`start_recording`/`stop_recording` are a **trajectory recorder for
driver-issued actions only** (per-action turn folders with before/after
screenshots + `action.json`; optional `record_video: true` → full-screen
mp4, ffmpeg 8.1.1 is on PATH). It does **NOT** capture the human's
physical mouse/keyboard. User-input capture is module 2's job.

## 3. Module 1 — mark-engine (TypeScript, Node ≥ 20, zero deps beyond node:child_process)

Wraps the driver into a mark vocabulary.

Contract (exported functions AND the CLI in §5):
```ts
mark(m: { kind: "circle"|"box"|"dot"|"label";
          x: number; y: number;          // screen px, mark center
          w?: number; h?: number;        // box only
          label?: string; color?: string; id?: string }): string  // returns mark id
acknowledge(id: string): void   // mark turns green, fades after ~1.5 s
clear(id?: string): void        // one mark or all
armed(on: boolean): void        // visible "teach mode armed" indicator (a labeled mark in a screen corner)
```

Implementation notes:
- One `cursor_id` per mark (`mark-<n>`), all in session `"asha"`.
- `mark()` = set_agent_cursor_motion (icon per kind, color, label,
  size from w/h, `idle_hide_ms: 0`, `arc_size: 0`, `glide_duration_ms: 50`)
  then move_cursor to (x, y).
- `acknowledge()` = set color `#22cc66`, then after 1.5 s clear the mark.
- Spawn `cua-driver call` per operation; start the daemon if `status` says
  it is not running.

Acceptance (write `demo/marks-demo.ts`):
1. Draws a box + a dot + a labeled circle at fixed coordinates; all three
   visible simultaneously and persist ≥ 60 s.
2. `acknowledge` turns the circle green and it fades.
3. `clear()` empties the screen; `end_session` on exit leaves no sprites.

## 4. Module 2 — teach-recorder (C#, single-file console app, .NET 8)

Captures the HUMAN's demonstration with semantic context.

- Global low-level hooks: `WH_MOUSE_LL` + `WH_KEYBOARD_LL`
  (SetWindowsHookEx). Console app, no window.
- Per mouse event, resolve semantic context via UI Automation
  `ElementFromPoint`: application (process name + pid), window title,
  ControlType, Name, AutomationId, and the element's bounding rect.
- Output: JSON Lines on stdout, one event per line:
```json
{"t":"2026-07-20T21:04:11.301Z","type":"click","x":812,"y":540,
 "app":"notepad.exe","pid":1234,"window":"Untitled - Notepad",
 "controlType":"MenuItem","name":"File","automationId":"FileMenu",
 "rect":[790,530,850,560]}
```
- Event types: `click`, `dblclick`, `rightclick`, `wheel` (with delta),
  `dragstart`/`dragend`, `text` (keystrokes BATCHED into one event per
  focus/element change — not one event per key), `hotkey` (modifier
  combos like ctrl+s), `wait` (emitted automatically when > 3 s pass
  between events — gaps are meaningful), `mode` (see below).
- Mode keys (global): **F8** = pointing mode (events tagged
  `"mode":"point"` — the user is SHOWING, not operating),
  **F9** = demonstrating mode (`"mode":"demo"`), **Esc** = stop, flush,
  exit 0. Emit a `mode` event on every switch.
- **Privacy, non-negotiable:** if the focused element has
  `IsPassword = true`, emit `{"type":"redacted"}` instead of the text —
  never the keystrokes. No file writing; stdout only — the host decides
  persistence.

Acceptance:
1. Run the exe, click through Notepad's File menu, type a sentence: the
   JSONL shows semantic fields (app, controlType, name) on clicks and ONE
   batched `text` event.
2. Type into a browser password field: output contains `redacted`, never
   the characters.
3. F8/F9 switch the `mode` tag; Esc exits cleanly.

## 5. Module 3 — recipe-compiler (TypeScript)

Turns a recorded demonstration into a semantic recipe via GPT-5.6.

- Input: the JSONL from module 2 + a one-line task title.
- Call the OpenAI API (model: `gpt-5.6`, key from `OPENAI_API_KEY`).
- The system prompt (draft it, keep it in `prompts/compile.md`, iterate):
  merge consecutive low-level events into intentional steps; drop
  incidental movement; every step gets: `intent` (imperative sentence),
  `targets` (ordered address list: uia identity → visible text →
  relation to neighbors → region; RAW COORDINATES ONLY AS LAST-RESORT
  EVIDENCE), `action`, `input?`, `expected` (what observably changes —
  window title, new element, text present).
- Output schema (`recipe.schema.json`, validate before returning):
```json
{ "title": "...", "steps": [
  { "intent": "Open the File menu",
    "targets": [{"by":"uia","value":{"controlType":"MenuItem","name":"File"}},
                {"by":"text","value":"File"},
                {"by":"region","value":[790,530,850,560]}],
    "action": "click",
    "expected": {"newElement":"menu 'File' expanded"} } ] }
```
- The recipe is a NEUTRAL object: no provider- or host-specific fields.

Acceptance: `fixtures/demo-notepad.jsonl` (ship a hand-written sample) →
`compile` produces schema-valid JSON with ≥ 3 steps, none of which uses a
raw coordinate as its FIRST target.

## 6. CLI contract (what the host harness calls)

```
asha mark '<json>'          → {"id":"mark-3"}
asha ack <id> | clear [id]  → {"ok":true}
asha armed on|off           → {"ok":true}
asha teach start            → streams recorder JSONL to stdout until Esc
asha compile <file.jsonl> --title "..." → recipe JSON on stdout
```
stdout is always machine-readable JSON (or JSONL); human noise to stderr.

## 7. Demo priorities (video deadline: tomorrow morning)

- **P0 (must):** marks demo live on screen · teach-recorder captures a
  real 5-step workflow · compiler emits the recipe. Each independently
  runnable — the video cuts between them.
- **P1 (strong):** host replays the recipe with Show-Me marks before each
  step (the host side exists; you provide the CLI faithfully and it works).
- **P2 (skip unless time):** freehand circles, arrows, text-span marks.

Build order: M1 → M2 → M3. Commit small and often. When the core works,
run `/feedback` in the Codex session and save the session ID — the
submission form wants it.
