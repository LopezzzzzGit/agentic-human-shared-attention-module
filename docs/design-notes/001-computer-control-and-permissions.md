# 001 — ASHA computer-control and conversational permission design

**Status:** Planned  
**Decision:** Accepted for implementation  
**Last reviewed:** 2026-07-23

This note defines how ASHA should demonstrate, interact with, and control a
person's desktop without confusing a visual cursor with the person's physical
mouse. It also defines how settings can be changed through conversation and
how the person can stop an action immediately.

The central design rule is:

> Separate what ASHA may do, how an action is delivered, whether ASHA's cursor
> is visible, and whether control is active right now.

Those are related decisions, but they are not the same setting.

## Goals

- Let the person continue using their physical mouse while ASHA interacts
  through a background-capable virtual cursor.
- Keep demonstrations possible without performing an action.
- Make every control capability explicit, initially disabled, and visible in
  settings.
- Prefer non-interfering background interaction over physical input.
- Never silently fall back from virtual interaction to the physical cursor.
- Let natural conversation propose settings changes without keyword parsers
  or hardcoded command sentences.
- Make voice approvals visible and auditable through a confirmation card.
- Provide a local, model-independent emergency stop.

## Terminology

### Demonstrator pointer

A static visual pointer or cue placed at a target. It communicates attention
but does not act.

### Ghost cursor

A visible, animated cursor that can move and demonstrate a path but cannot
interact with the target application.

### Virtual interaction cursor

ASHA's logical cursor for real background interaction. It may click,
double-click, right-click, drag, and scroll without moving the person's
physical pointer when the target application supports background delivery.
Its visual overlay can be shown or hidden independently of its ability to act.

### Physical cursor

The single Windows system pointer shared with the person. Using it can move
the person's pointer, change foreground focus, and interfere with concurrent
work. It therefore requires a separate permission.

### Capability policy

The global or profile-level boundary describing what ASHA is allowed to
request and use. A profile may narrow a global policy but must not silently
widen it.

### Control lease

A visible, temporary activation of permitted computer-control capabilities
for one session. Enabling a capability globally does not create a permanent
control lease.

## Current implementation reality

The installed `cua-driver 0.8.3` already provides most of the virtual
interaction transport:

- `move_cursor` moves an independent agent-cursor overlay and does not move the
  physical pointer.
- `click` prefers a cached UI Automation element or a background UI Automation
  hit-test. It can fall back to background window messages without stealing
  focus.
- `double_click`, `right_click`, `drag`, and `scroll` expose background-capable
  operations.
- Agent cursors are instanced by session and can be styled, animated, shown,
  or hidden.
- `delivery_mode: "foreground"` is an explicit escalation for applications
  that cannot accept background delivery.

The driver does not currently expose a general background-hover tool.
Application-specific browser automation may provide hover semantics, but ASHA
must not advertise universal virtual hover until it has been implemented and
verified.

ASHA is not yet routing ordinary desktop actions through these CUA background
tools. The current
[`DesktopControlExecutor`](../interactive/DesktopControlExecutor.cs) calls
Windows `SetCursorPos` and `SendInput`, so it controls the physical pointer.
The existing [`mark-engine`](../src/mark-engine.ts) uses CUA as an overlay
fallback, while Windows normally uses ASHA's native overlay. Connecting the
CUA background action path is therefore an integration task, not a new cursor
invention.

## Settings hierarchy

The user-facing settings should be grouped under **Computer control**:

```text
Computer control

  Keyboard
    [ ] Allow keyboard interaction

  Mouse and cursor
    [ ] Enable virtual cursor
          Behaviour
          (•) Interact with controls
          ( ) Demonstrate only

          [✓] Show virtual cursor while it works

    [ ] Allow ASHA to use my physical cursor
          [✓] Ask before falling back from virtual to physical
```

The two top-level cursor permissions are toggles, not radio buttons. Both may
be allowed at the same time, although exactly one delivery method is selected
for an individual action.

### Defaults

| Setting | Default |
| --- | --- |
| Keyboard interaction | Off |
| Virtual cursor | Off |
| Virtual-cursor behaviour after enabling | Interact with controls |
| Show virtual cursor after enabling | On |
| Physical cursor | Off |
| Ask before physical fallback | On and mandatory by default |

The child settings remain disabled or hidden until the virtual cursor is
enabled. Their defaults are retained so the first activation produces a
visible, interactive virtual cursor.

### Virtual-cursor state matrix

| Interaction | Visibility | Result |
| --- | --- | --- |
| On | On | Visible virtual cursor that genuinely interacts |
| On | Off | Hidden virtual cursor that genuinely interacts |
| Off | On | Ghost/demonstrator cursor |
| Off | Off | No useful behaviour; warn or prevent this combination |

“Ghost cursor” therefore means visible but non-interacting. An interacting
cursor with visibility disabled is a **hidden virtual cursor**, not a ghost.

## Scope and activation

Global settings define the maximum capability policy. A profile may provide
safer defaults or remove capabilities for a particular context. Session
settings are temporary and disappear with the session.

A separate control lease determines whether allowed capabilities are active
now. The normal sequence is:

1. A capability is permitted in the effective global/profile policy.
2. The person starts or approves a visible control lease.
3. ASHA uses only the capabilities covered by that lease.
4. Ending or stopping the lease immediately removes active control without
   rewriting the person's global settings.

Conversational changes default to the current session unless the person
explicitly asks for a profile or global change.

## Action-routing policy

For each requested action, ASHA should:

1. Infer the person's intent through the model and resolve a current target
   from visual evidence, UI metadata, or a selected cue.
2. Check the effective capability policy and active control lease.
3. If the request is demonstrative, move or place the visual cursor without
   dispatching input.
4. If virtual interaction is permitted, try the CUA background action first.
5. Verify the resulting application state before claiming success.
6. If the driver returns `background_unavailable`, stop and explain that the
   application requires physical/foreground control.
7. Propose physical-cursor use and obtain confirmation. Never escalate merely
   because an application “looks like” a framework that might reject
   background input.
8. Execute the physical action only after the required permission and lease
   are present, then verify again.

If both cursor capabilities are enabled, virtual interaction remains the
preferred delivery method. A setting that allows the physical cursor does not
authorize silent fallback.

## Conversational settings changes

Ordinary settings requests must be interpreted semantically by the model and
converted to a structured proposed change. They must not be implemented as a
list of phrases such as “use real mouse” or “enable cursor.”

The settings runtime owns the state transition. The model may propose a
change, but it cannot directly mutate the setting.

### Propose–confirm–commit

Every permission-expanding voice request follows a two-turn confirmation:

1. **Explain** — state the current value, proposed value, effect, and scope.
2. **Ask** — request explicit permission without applying the change.
3. **Guide** — explain that a simple affirmative response or the visible
   Confirm button will approve it.
4. **Commit or cancel** — interpret the next response in relation to the
   pending proposal.
5. **Report** — verify the actual setting and report the resulting value.

Example:

> That would let me interact with applications through the virtual cursor
> without moving your physical mouse. It is currently disabled. Shall I enable
> it for this session? Say yes, go ahead, do it, or another clear
> confirmation.

The examples teach the interaction; they are not a hardcoded acceptance list.

### Pending-change record

A proposed change should have a stable identifier and contain at least:

- setting identifier;
- previous value;
- proposed value;
- scope;
- risk or permission category;
- creation and expiry times;
- originating conversation turn.

Only one conflicting proposal may be pending for a setting. Applying a
proposal must be idempotent, so a spoken approval and a simultaneous button
press cannot apply it twice.

### Confirmation rules

- The original request never confirms itself. Approval requires a second user
  turn or an explicit UI action.
- Confirmation is evaluated semantically against the pending proposal, not
  through an unrestricted substring match.
- Silence, unrelated speech, background audio, or an uncertain transcript do
  not confirm.
- ASHA's own TTS output must never be transcribed as user confirmation.
- A changed scope such as “only for this session” creates a revised proposal
  that ASHA restates before applying.
- A proposal expires after a short timeout, a subject change, session end, or
  any external change to the same setting.
- Turning off or revoking a capability is immediate and does not require
  confirmation.

## Confirmation-card lifecycle

The card is a temporary view of the settings transaction. The settings store
remains the single source of truth.

### Pending

The card displays the exact proposed setting and scope, with **Confirm** and
**Cancel** buttons. No setting has changed.

### Verbal or button approval

The card locks both buttons and changes to **Applying…**. After the settings
store reports the new effective value, the corresponding toggle updates and
the card briefly shows a successful result.

### Completion

The success card fades away after a few seconds. Activity retains the
auditable event. A separate Undo button is unnecessary because the setting
can be revoked directly in settings or through conversation.

### Rejection or uncertainty

- A clear rejection shows “Cancelled—nothing changed” briefly and then fades.
- An uncertain transcript keeps the proposal pending and allows another
  spoken response or a button press.
- If the setting changes elsewhere while the card is pending, the card is
  invalidated and disappears instead of applying stale state.

## Immediate cancellation and emergency stop

Cancellation must be available independently of the model, provider, and
network.

### Stop scopes

1. **Stop action** — cancel the current operation and clear its queued
   follow-up actions.
2. **Stop mouse** — stop pointer activity and suspend the pointer-control
   lease while leaving conversation and vision available.
3. **Stop computer control** — stop pointer and keyboard activity, clear all
   control queues, and revoke the active control lease.

During active control, an unqualified “stop” takes the safest interpretation:
stop all current control activity and pause the lease.

### Local emergency path

ASHA should provide:

- a persistent **Stop control** button whenever a control lease is active;
- a system-wide emergency hotkey;
- a reserved local emergency utterance such as “ASHA, stop.”

Natural stop requests may still be interpreted semantically, but the reserved
utterance bypasses the reasoning model. This is a deliberate safety exception
to the no-phrase-parser rule: it must work while the model is busy, confused,
rate-limited, or offline.

An emergency stop should:

- reject new control actions immediately;
- cancel the current cancellable operation;
- clear queued actions;
- release any held mouse button or keyboard key;
- halt virtual-cursor animation;
- invalidate and ignore late model or tool results;
- suspend or revoke the appropriate active lease;
- record that the person stopped control.

An already delivered click cannot be undone automatically. Stop prevents
pending work and safely terminates actions still in progress.

### Button wording

The pending settings card uses **Cancel** because it cancels a proposal.
An active action card uses **Stop action** because it stops execution. The
persistent emergency control uses **Stop control**. Distinct labels prevent
three different scopes from looking like the same operation.

## Concurrency and interference

Background delivery prevents physical-pointer movement and often avoids
foreground focus changes. It cannot make two actors' changes to the same
application logically independent.

- The person can continue using the physical pointer while ASHA works through
  a virtual cursor.
- Concurrent work in different windows is the safest case.
- If the person and ASHA manipulate the same window or control at once, their
  state changes may conflict.
- ASHA should detect fresh human activity in its target window and pause,
  re-ground, or ask before continuing.
- A future per-window action lease can serialize conflicting actions without
  blocking unrelated desktop work.

## Activity and audit record

For every proposed setting change or computer action, retain the meaningful
event rather than raw high-frequency cursor telemetry:

- actor and originating turn;
- requested intent and resolved target;
- effective capability policy and lease;
- virtual, physical, or demonstrative delivery mode;
- cursor visibility;
- proposal, confirmation, rejection, or expiry;
- before/after setting values and scope;
- execution outcome and verification evidence;
- cancellation or emergency-stop event.

Sensitive text, credentials, and unbounded screenshots remain governed by
their separate privacy and retention policies.

## Related speech setting: Teach ASHA new words

The settings hierarchy should also expose a small **Teach ASHA new words…**
action under Speech. This is contextual speech vocabulary, not model
fine-tuning or speaker training.

Each local vocabulary entry may contain:

- canonical spelling;
- optional spoken aliases;
- global, profile, project, or session scope.

The installed `faster-whisper` supports `hotwords` and `initial_prompt`, though
the current local STT endpoint does not yet pass them through. A future
implementation should send a bounded, context-relevant vocabulary set to
Whisper and use only cautious, explicit alias corrections afterward. The
vocabulary remains local and outside the repository.

## Implementation sequence

1. Add the capability-policy and control-lease state models.
2. Add the settings hierarchy and defaults without changing action delivery.
3. Add structured pending changes and the synchronized confirmation card.
4. Add the local emergency-stop controller and cancellation propagation.
5. Implement a CUA background-action adapter and route virtual actions through
   it.
6. Keep physical `SetCursorPos`/`SendInput` as an explicitly permitted fallback.
7. Add post-action verification and stale-target handling.
8. Add virtual hover only after a reliable background implementation exists.
9. Add the local speech-vocabulary setting and STT transport.

## Acceptance criteria

The design is implemented when:

1. All computer-control capabilities are off by default.
2. Enabling the virtual cursor defaults to interactive and visible.
3. A supported virtual click does not move the person's physical pointer or
   steal foreground focus.
4. Hiding the virtual cursor does not disable its permitted interaction.
5. Demonstration-only mode never changes the target application.
6. A background-delivery failure never silently escalates to physical input.
7. A voice-requested permission expansion changes nothing before a distinct
   confirmation.
8. Spoken and clicked confirmation share one idempotent pending transaction.
9. The settings toggle, not the card or transcript, is the source of truth.
10. Stop control works locally while the model and provider are unavailable.
11. Stopping a drag or held key releases the input safely and ignores late
    tool results.
12. Session leases do not survive session end, while intentional global
    settings do.
13. Ordinary language is resolved through structured model intent rather than
    hardcoded command sentences.
