# ASHA current implementation status

**Last reviewed:** 2026-07-30

**Reference branch:** `codex/post-action-verification`

**Pull request:** [#2 — Harden ASHA desktop control and session lifecycle](https://github.com/LopezzzzzGit/agentic-human-shared-attention-module/pull/2)

This page is the honest boundary between what ASHA can demonstrate now and
what remains planned. The roadmap describes product direction; numbered design
notes preserve decisions; this page records the current implementation.

## Implemented and covered by automated tests

### Conversation, speech, and memory

- A borderless, draggable ASHA orb with a compact expandable control panel.
- Local speech-to-text and text-to-speech through the configured speech
  service, with typed chat as an exact-input alternative.
- Durable-by-default conversations, explicit temporary conversations, and
  Keep, Discard, and Cancel lifecycle choices.
- Full local conversation logs plus rolling session summaries.
- Scoped personal speech vocabulary with explicit aliases.
- Model-provider key rotation, cooldown handling, and reasoning-block removal.

### Shared attention and visual evidence

- Local desktop captures at pointer, foreground-window, directional-screen,
  and entire-desktop scopes.
- Model-requested views that are not constrained to the person's pointer.
- Local OCR and an isolated Windows UI Automation snapshot.
- Coordinate mapping between compressed provider images, full-resolution local
  evidence, and the current desktop.
- Click-through dots, circles, boxes, arrows, labels, and cue removal.
- Targeted detail requests for explicit close-ups and detailed reading.
- Protected ASHA pixels are masked before capture evidence can be stored or
  sent to a provider.

### Permission-gated desktop control

- Installed-application and running-window discovery without per-application
  command recipes.
- Generic application launch, window activation, approved folder opening,
  keyboard navigation, virtual interaction, and separately gated physical
  input.
- Capability policies, session-scoped control leases, approval transactions,
  emergency stop, and immediate revocation.
- ASHA self-target exclusion across physical input, UI Automation, window
  activation, keyboard delivery, and capture.
- Semantic UI operations including select, open, invoke, activate, expand, and
  collapse.
- Before-and-after verification using target state, foreground identity,
  snapshot identity, and bounded visual change evidence.
- Truthful partial outcomes when an application opens but Windows keeps it in
  the background.

### Verification

- 125 .NET reliability, intent, policy, session, grounding, accessibility, and
  computer-control tests.
- 10 Node mark-engine and semantic-session tests.
- Release builds for the overlay, UI Automation worker, and ASHA live app.

## Working but incomplete

- Text targets and accessible controls ground substantially better than
  unlabeled visual objects.
- Browser tabs can be selected through UI Automation once resolved, but the
  general desktop → window → tab/document → control hierarchy is incomplete.
- Virtual cursor, background UI Automation, and physical input exist, but an
  explicitly requested delivery method is not yet enforced consistently.
- Post-action verification distinguishes many real state transitions, but not
  every application exposes enough accessibility state.
- Live visual awareness works, but repeated screenshots and model phases can
  still consume provider rate limits too quickly.
- Local speech vocabulary works, while speech-service health and automatic
  recovery remain basic.

## Known gaps observed in live testing

1. A descriptive target may be mistaken for a literal window title instead of
   being resolved across application windows, browser tabs, and documents.
2. Non-text annotation targets such as a person's eyes can receive an
   ungrounded model coordinate. Drawing a cue currently proves only that the
   cue rendered, not that the visual object was located correctly.
3. Rejected guidance is not yet a first-class correction operation that moves
   or replaces the previous cue.
4. Plural visual targets do not yet produce a cardinality-aware set of cues.
5. A provider that narrates instead of emitting a required tool call can cause
   a failed turn rather than a bounded structured repair.
6. Provider calls do not yet reuse every unchanged scene, and broad captures
   are still requested more often than necessary.
7. ASHA does not yet expose an online-research capability with local query
   minimization and user-controlled disclosure.
8. The compact panel still carries settings that belong in a searchable,
   scrollable, full settings window.

## Next implementation tranche

The next reliability tranche is deliberately application-agnostic:

1. replay the latest live session as a regression fixture;
2. separate target grounding, cue rendering, input delivery, and verified
   outcome in both telemetry and spoken claims;
3. add overview → region → detail → ground → mark-or-decline annotation;
4. add cue correction, replacement, and multi-target geometry;
5. add a platform-neutral surface resolver;
6. enforce the person's chosen interaction executor;
7. add bounded malformed-tool recovery and provider backpressure; and
8. cache unchanged evidence and prefer small detail crops.

No application name, account, screen resolution, monitor arrangement, or
personal directory may become an execution recipe. Personal examples belong
in regression fixtures only.
