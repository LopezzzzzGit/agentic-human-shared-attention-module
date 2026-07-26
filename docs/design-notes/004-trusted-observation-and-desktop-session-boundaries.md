# 004 — Trusted observation and desktop-session boundaries

**Status:** Implemented; live acceptance pending
**Decision:** Accepted for Phase Two implementation
**Last reviewed:** 2026-07-25
**Related:** [003 — Protected control plane and self-target exclusion](003-protected-control-plane-and-self-target-exclusion.md)

## Decision

ASHA must not turn its protected control plane into model input, and temporary
computer-control authority must not survive a change in the trust state of the
Windows desktop session.

Phase Two adds two permanent boundaries:

1. **Protected observation exclusion** — every ASHA-owned window is redacted
   before a pixel capture can enter memory, local evidence, OCR, or a provider
   request. ASHA-owned UI Automation trees are excluded as well.
2. **Desktop-session trust boundary** — locking or disconnecting Windows, or
   entering a Windows remote desktop session, immediately ends conversation,
   pauses observation, clears pending evidence, and revokes computer control.

Neither boundary relies on model instructions.

## Why this is separate from Phase One

Phase One prevents ASHA from operating its own interface. It does not by
itself prevent a screenshot or accessibility snapshot from exposing text in
ASHA's composer to the model. It also does not establish that a control lease
should remain valid after Windows changes from a locally attended console to a
locked, disconnected, or remote session.

Phase Two closes those gaps without redesigning the action architecture.

## Terminology

- **Protected observation exclusion** — removing ASHA-owned pixels and
  semantic UI data before either becomes model-visible evidence.
- **Desktop-session trust** — whether the active Windows session is a local,
  unlocked, interactive console.
- **Trust transition** — a Windows session switch such as lock, disconnect,
  remote connect, local console connect, or unlock.
- **Capture redaction** — an opaque replacement for the intersection between
  a capture and an ASHA-owned top-level window.

## Pixel capture invariant

All capture paths use the same redacting primitive:

```text
enumerate ASHA windows
        |
        v
capture requested desktop pixels
        |
        v
enumerate ASHA windows again
        |
        v
redact the union of before/after ASHA bounds
        |
        v
ring buffer / local evidence / OCR / provider encoding
```

Redaction happens before an image is written, encoded, returned, compared, or
stored in the in-memory ring. Enumerating both before and after capture covers
an ASHA window that moves during acquisition. If protected-window enumeration
fails, the capture fails closed rather than returning potentially exposed
pixels.

The redaction is mapped from desktop coordinates into the scaled capture, so
it remains correct for an entire desktop, one monitor side, a foreground
window, or a detail crop.

## Semantic capture invariant

Windows UI Automation may expose composer text even when pixels are redacted.
The isolated UI Automation worker therefore receives ASHA's protected process
identifier for both inspection and action requests. It returns no tree when
the selected root belongs to ASHA.

This check occurs in both the host and isolated worker so a focus change
between those processes cannot expose the protected tree.

## Desktop-session trust policy

The safe state is a local, unlocked, interactive Windows console.

The following transitions revoke trust:

- session lock or logoff;
- console disconnect;
- Windows remote desktop connect;
- remote desktop disconnect until a trusted local console is established.

Revocation:

- stops and discards unfinished microphone capture;
- cancels an in-flight conversational turn;
- stops pixel sampling and desktop-awareness inspection;
- clears pending and most-recent visual evidence from runtime memory;
- ends the current CUA session and computer-control lease;
- removes the computer-control presence frame;
- records a bounded local security event when a retained session exists.

Returning to an unlocked local console may restart the configured local
awareness mode for a retained session. It never restores a previous computer
control lease or conversation automatically. The person must deliberately
restart those capabilities.

Starting ASHA inside a Windows terminal-services session begins in the
untrusted state.

## Honest remote-control boundary

Windows RDP exposes session transitions that ASHA can observe. Products such
as Chrome Remote Desktop, AnyDesk, TeamViewer, or vendor-specific support
tools can inject input into the ordinary console session and may not expose a
reliable standard Windows session signal.

Phase Two does not claim to identify every third-party remote-control product.
Those products remain responsible for their own authentication. A later
explicit remote-use mode and stronger local-presence approvals may add
product-specific adapters, but must never weaken Phase One self-target
exclusion.

## Human experience

- ASHA remains visible to the person, but appears as a neutral redacted area
  in any image ASHA itself receives.
- Locking or changing to RDP ends control calmly rather than crashing ASHA.
- Unlocking locally restores configured awareness, not authority.
- Conversation and control do not silently resume.
- Local session logs state why capabilities paused without storing protected
  composer content.

## Phase Two implementation

1. Add a reusable protected-window capture masker.
2. Apply it inside the single low-level screen-capture primitive.
3. Deny ASHA-owned UI Automation inspection in host and worker.
4. Add a pure desktop-session trust policy.
5. subscribe the ASHA window to Windows session-switch notifications.
6. Revoke voice, vision, and computer control on unsafe transitions.
7. Prevent new control leases and visual captures while untrusted.
8. Restore local awareness only after a trusted local return.
9. Add geometry, semantic-exclusion, and trust-transition tests.

## Acceptance criteria

Phase Two is implemented when:

1. Attached chat, detached chat, settings, popups, and the orb are opaque in
   every captured image.
2. Redaction is correct for scaled full-screen and partial-region captures.
3. A moving ASHA window cannot escape redaction during acquisition.
4. ASHA's UI Automation tree is never returned to model context.
5. Lock, disconnect, logoff, and Windows RDP revoke computer control.
6. Those transitions stop observation and conversation and clear cached
   visual evidence.
7. A control lease or screenshot cannot start while the session is untrusted.
8. Local unlock may resume awareness but never silently restores control.
9. Non-ASHA captures and ordinary local human interaction continue to work.

## Later work

Third-party remote-control detection, an explicit remote-use profile, secure
desktop confirmation, sensitive-field masking in non-ASHA applications, and
general prompt-injection treatment for untrusted screen content remain later
phases.
