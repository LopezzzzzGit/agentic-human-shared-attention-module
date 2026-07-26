# 006 — Durable and temporary session lifecycle

**Status:** Implemented; live acceptance pending
**Decision:** Accepted for implementation
**Last reviewed:** 2026-07-26
**Related:** [001 — Computer control and conversational permissions](001-computer-control-and-permissions.md), [005 — Approval transactions and emergency stop](005-approval-transactions-and-emergency-stop.md)

## Decision

ASHA uses a durable-by-default conversation model. A new ordinary
conversation starts a new retained session with empty conversational and
action state. An existing retained session is loaded only after the person
explicitly chooses **Continue**.

ASHA also offers a **Temporary session**. It has full working conversational
context while active but is not added to the retained session library unless
the person chooses **Keep this session**.

Hiding ASHA in the system tray keeps the current process and session active.
Fully quitting or restarting ASHA does not silently reactivate the last
retained session. The last session remains visible in the session library.

## Terminology

- **Retained session** — an autosaved conversation and semantic timeline with
  durable local memory.
- **Temporary session** — an ephemeral conversation and working timeline that
  is discarded unless explicitly promoted.
- **Promotion** — converting a temporary session into a retained session.
- **Active session** — the one session currently loaded into ASHA's process.
- **Recent session** — a retained session remembered for discovery but not
  automatically loaded.
- **Session boundary** — a hard reset of conversational history, unresolved
  clarification, verified-target memory, vision evidence, and action intent.

## Session states

| State | Saved automatically | Loaded automatically after restart | Can be continued later |
| --- | --- | --- | --- |
| No active session | No | No | Not applicable |
| Retained session | Yes | No | Yes |
| Temporary session | No | No | Only after promotion |

## Entry points

The expanded ASHA panel and session library expose:

- **New session** — ends the current session after resolving any temporary
  session, then creates a blank retained session.
- **Temporary session** — creates a blank ephemeral session.
- **Sessions** — opens retained history. Continuing is explicit.
- **Keep this session** — promotes the active temporary session.
- **End session** — saves and closes a retained session, or asks whether a
  temporary session should be kept or discarded.

Tapping the orb with no active session is equivalent to **New session**, then
begins listening.

## Temporary storage

Temporary conversation and semantic events remain in process memory. Local
visual evidence may use a session-scoped temporary directory so the existing
coordinate and verification pipeline remains unchanged. Discarding deletes
that exact temporary directory. Startup removes orphaned temporary-session
directories left by an interrupted process.

Promotion creates one retained ledger session using the existing temporary
identifier, writes the buffered transcript and semantic events, and keeps its
session-scoped evidence paths valid.

## Quit behaviour

Retained sessions are already autosaved turn by turn. Quitting makes the
session inactive but does not discard it.

Quitting, switching, or ending a non-empty temporary session presents:

- **Keep session**
- **Discard**
- **Cancel**

An empty temporary session may be discarded without a second prompt.

## Action-state isolation

Every new, resumed, promoted, discarded, or ended session boundary:

1. cancels pending approval transactions;
2. ends any computer-control lease;
3. clears current model conversation memory;
4. clears pending desktop requests and clarifications;
5. clears verified repeat-target memory;
6. invalidates current screenshots, UI snapshots, and awareness summaries.

Starting computer control grants capabilities only. It never resumes a
previous desktop objective.

The model receives tools according to the current turn. Retained narrative
memory is context, never executable authority.

## Acceptance criteria

1. A greeting after application restart cannot continue the prior session's
   unfinished desktop request.
2. Tapping the orb with no active session creates a new retained session.
3. The last retained session remains in the library after restart but is not
   loaded automatically.
4. Continuing a session is an explicit action and loads its transcript and
   derived memory.
5. Temporary chat is absent from the library before promotion.
6. Keeping a temporary session retains its transcript, semantic events, and
   evidence references.
7. Discarding removes temporary conversation, working state, and its exact
   temporary evidence directory.
8. Hiding to the tray does not end or switch the active session.
9. Session state is always visible in the expanded panel.
10. No application-specific phrase or target is used to determine a session
    boundary.
