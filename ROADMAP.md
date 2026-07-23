# ASHA product roadmap

This roadmap records product decisions that are larger than the current
demonstrator. It separates the intended architecture from features that are
already implemented, so an experimental button is never mistaken for the
final interaction model.

## Product principle: memory with human agency

ASHA is not designed as a forgetful command assistant. A conversation,
demonstration, visual observation, and resulting desktop action can form one
meaningful work episode and should remain connected.

Persistence must nevertheless be a human choice. ASHA may use working memory
while she is running without silently turning every casual utterance, screen
view, or pointer movement into permanent history.

The normal policy is therefore **ask at the end**:

1. ASHA starts or continues a bounded work session.
2. Conversation, visual context, cues, actions, and outcomes share one local
   timeline while that session is active.
3. When the person ends the session or quits ASHA, ASHA asks whether to keep
   it, discard it, or cancel closing.
4. A kept session can be named and placed in a project. A discarded session
   is deleted rather than converted into project memory.

## Vocabulary and ownership

- **Project** — a durable container for related work, sessions, curated
  knowledge, procedures, and links to external harnesses.
- **Session** — one bounded conversation or work episode. It can be active,
  retained, or discarded.
- **Ephemeral session** — a real session with short-term memory whose retention
  decision has not yet been made. This is the precise form of “sessionless
  mode”; it does not mean that ASHA forgets every turn while running.
- **Working memory** — recent dialogue, current visual scene, unresolved
  references, active permissions, and pending actions needed for the current
  interaction.
- **Session memory** — the retained timeline and summaries needed to continue
  a particular session later.
- **Project memory** — curated, reusable knowledge extracted from retained
  sessions. It must not be an unfiltered concatenation of every transcript.
- **Procedure or recipe** — an editable learned workflow compiled from a
  retained session. It is semantic and resilient to window movement; raw
  coordinates remain evidence, not the procedure itself.

When ASHA is the conversation coordinator, the retained ASHA session owns its
transcript. When Codex, Claude, Lantern, or another harness owns the
conversation, ASHA stores a stable harness link plus the shared-attention
events it needs; it does not create a second conflicting transcript.

## Session lifecycle

### Starting

The person can:

- start a new ephemeral session;
- continue a retained session;
- start a new session directly inside a selected project; or
- converse without choosing a project and decide where to keep the result
  later.

Starting a session also starts ASHA when she is not already running. Starting
ordinary voice listening does not by itself imply permanent retention.

### Ending

An active, not-yet-retained session ends with a clear dialogue:

- **Keep session** — retain it locally; choose or confirm its title and project;
- **Discard session** — delete its draft conversation, visual observations,
  and uncurated events;
- **Cancel** — return to the active session.

A previously retained session saves new work back into itself when continued,
but destructive removal remains a separate explicit action.

### Default retention policies

The demonstrator defaults to **Ask every time**. Later, an advanced privacy
setting may offer:

- Ask every time;
- Always keep locally; or
- Always discard on close.

Always-discard is intended for kiosks, shared office demonstrators, sensitive
temporary work, and people who deliberately want a stateless assistant. It
must have a persistent visible indicator so nobody mistakes it for durable
learning.

Unexpected shutdown recovery should use a small local draft journal. Keeping
that recovered draft still requires the normal retention decision. A strict
always-discard profile may disable recovery journaling entirely.

## Memory architecture

The provider's maximum context window is capacity, not a memory design. ASHA
should assemble each model request from several bounded layers:

1. identity, policies, permissions, and current profile;
2. recent conversation turns verbatim, limited by a token budget rather than
   a fixed number of messages;
3. a rolling summary of earlier turns in the active session;
4. recent visual observations, cues, actions, and verified outcomes;
5. relevant session facts and unresolved references;
6. retrieved project knowledge or learned procedures only when relevant.

The full raw session remains locally reviewable, but ordinary inference does
not resend the entire transcript on every turn. Retained sessions must
rehydrate these memory layers when continued after an ASHA restart.

### Visual memory

Visual information has different lifetimes:

- **Current scene** — the latest adaptive view and application focus; seconds
  old and replaceable.
- **Observation event** — a meaningful visual change connected to dialogue,
  a cue, an action, or an outcome.
- **Semantic anchor** — a named target such as “GMX Inbox”, combining visual
  description, window identity, accessibility data, relative position, and a
  reference crop.
- **Recording artifact** — raw screenshots or video retained only through an
  explicit recording policy.

Continuous sampling is not continuous permanent recording. Unchanged frames
are skipped, transient frames stay local, and provider-bound keyframes remain
visible and permission-gated.

## Planned delivery order

### P0 — reliable working and session memory

- Queue the newest pending visual change while ASHA is listening or speaking;
  refresh it after the voice turn instead of dropping it.
- Replace the eight-exchange in-memory limit with a token-budgeted recent-turn
  window.
- Load retained session conversation when a session is continued or ASHA is
  restarted.
- Maintain a rolling session summary and a small structured visual/action
  memory.
- Keep current visual context available until replaced or explicitly cleared;
  mark it stale rather than erasing it after an arbitrary short timeout.
- Add the Keep session / Discard session / Cancel ending dialogue.

### P1 — visible project and session management

- Project picker and session browser in ASHA's settings.
- New, continue, rename, move to project, archive, export, and delete.
- Clear distinction between an active draft, a retained session, and curated
  project memory.
- Reviewable timeline combining speech, cues, visual evidence, input events,
  approvals, actions, and outcomes.

### P1 — cue-aware visual grounding and control

- Resolve capture scope in this order: explicit region, selected cue, gesture
  path, pointer, active window, whole display.
- Convert dots, boxes, arrows, and natural mouse actions into spatial input for
  both human teaching and model guidance.
- Validate the current topmost surface before physical input.
- Show every model-controlled pointer action and capture a verification view.
- Store learned targets as semantic anchors rather than fixed coordinates.

### P1 — teaching and procedure memory

- Curate a historical session timeline into an editable procedure candidate.
- Preserve the connection between spoken explanation and demonstrated input.
- Allow cue labels, targets, order, expected outcomes, and mistakes to be
  corrected before compiling a recipe.
- Replay through accessibility APIs when reliable and visual/physical control
  when necessary, with scoped approval.

### P2 — harnesses, agents, and shared channels

- Provider-neutral ASHA skill/plugin for Codex, Claude, Lantern, Hermes-style
  runtimes, and local tool-calling models.
- A harness may delegate desktop control to ASHA or subscribe to selected
  project/session memory and act through ASHA's tools itself.
- Curated project channels can be shared across agents while personal memory,
  credentials, and excluded applications remain private.
- Agent-to-agent coordination remains human-facing and reviewable.

## Near-term acceptance criteria

The memory foundation is ready when all of the following are true:

1. ASHA remembers a reference made more than eight exchanges earlier in the
   same active session.
2. A retained session can be closed, ASHA restarted, and the conversation
   continued without behaving like a new assistant.
3. Live awareness continues through a long multi-turn voice conversation and
   processes the latest screen change after speech finishes.
4. Ending an unretained session always offers Keep, Discard, and Cancel.
5. Discard removes the draft; Keep makes it visible in the selected project.
6. Ordinary inference receives compact relevant memory, not an unbounded raw
   telemetry or screenshot stream.
