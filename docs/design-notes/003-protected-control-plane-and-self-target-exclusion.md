# 003 — Protected control plane and self-target exclusion

**Status:** Implemented; live acceptance pending
**Decision:** Accepted for Phase One implementation
**Last reviewed:** 2026-07-25
**Related:** [001 — Computer control and conversational permissions](001-computer-control-and-permissions.md)

## Decision

ASHA's own windows and controls form a protected human control plane. No
model-originated desktop action may target that control plane through physical
input, CUA background interaction, Windows UI Automation, window management,
or application launching.

The invariant is:

> ASHA may explain or display its interface, but ASHA cannot operate, type
> into, submit, reconfigure, hide, close, or grant itself permission through
> its general desktop-control capabilities.

This is a runtime security boundary, not a model instruction. Prompts,
conversation, screenshots, remembered procedures, and tool arguments cannot
weaken it.

## Threat model

Phase One addresses:

- self-prompting through ASHA's attached or detached composer;
- a model clicking Send after placing text in a composer;
- a model changing its own capability settings or approval controls;
- stale foreground state causing keyboard input to land in ASHA;
- CUA or UI Automation bypassing a check made only for physical input;
- a target changing between grounding and physical delivery;
- launching or managing ASHA through its general application/window tools.

Phase One does not claim to stop malware or an independently authenticated
remote-control product that already controls the person's logged-in Windows
session. Those systems have their own trust boundary. ASHA must nevertheless
avoid privilege amplification: conversation alone cannot expand the
capabilities available in the current policy and control lease.

## Terminology

- **Protected control plane** — all windows and controls owned by ASHA's
  process, including composers, Send buttons, settings, permissions, session
  controls, vocabulary, confirmation cards, and quit controls.
- **Self-target exclusion** — the immutable rule that rejects a desktop action
  whose live target belongs to ASHA.
- **Desktop action authorizer** — the model-independent runtime component that
  validates a live target and issues a short-lived action permit.
- **Action permit** — a runtime-owned, expiring capability for one exact
  normalized desktop action and its verified non-ASHA surfaces.
- **Execution-time revalidation** — checking the real target again immediately
  before foreground input rather than relying on cached awareness.

## Protected identity

Protection is based primarily on the current ASHA process identifier. Window
handles and the current executable identity provide additional checks.
Protection never depends on a visible title, screen coordinate, composer
position, or model-supplied application name.

The whole ASHA process is protected rather than only the composer rectangle.
This remains correct when the chat is detached, moved, resized, hidden, or
when new ASHA windows are added.

## Authorization flow

```text
model tool request
        |
        v
normalize and ground one current target
        |
        v
protected-surface policy
   |                    |
   | ASHA / unknown     | verified foreign surface
   v                    v
 deny             short-lived action permit
                              |
                              v
                  CUA / UIA / physical executor
                              |
                              v
                   post-action verification
```

Executors do not accept a raw model action as sufficient authority. The
physical executor validates the permit again. The CUA adapter also rejects the
protected process independently. The isolated UI Automation worker receives
the protected process identifier and refuses to invoke a pattern in it.

## Keyboard safety

Keyboard input is focus-sensitive and receives stricter checks:

1. Read the actual Windows foreground window immediately before authorization.
2. Never substitute ASHA's cached awareness foreground for this security check.
3. Reject ASHA, protected administrative surfaces, and unverifiable focus.
4. Re-read foreground identity immediately before delivery.
5. During multi-character physical text input, verify that focus still matches
   the permitted window before each character.
6. Abort the remaining input if focus changes.

This specifically closes the case where desktop awareness intentionally
ignores ASHA's floating window to preserve conversational context while the
physical keyboard would still type into the real focused composer.

## Human interaction

The protected-surface policy applies only to ASHA's computer-control
dispatchers. It does not alter normal WPF input, so the person may still:

- type and paste in either composer;
- press Enter or click Send;
- move and resize ASHA;
- change settings and answer confirmation cards;
- use the session library and vocabulary editor.

Safe self-facing features should use narrow internal application commands,
not simulated desktop input. For example, a future “show settings” capability
may open the settings view through an explicit internal API, but it cannot
click settings controls.

## Failure and audit behavior

Self-target checks fail closed before input delivery. The human-facing result
explains that ASHA cannot operate its own protected interface. The local
activity ledger records a bounded `control.action_denied` event with reason
`protected_self_surface`; it never records composer draft text.

An unverified or changed target is also rejected. No physical fallback follows
a protected, stale, or uncertain background result.

## Phase One implementation

1. Add the protected-surface policy and runtime-owned action permit.
2. Route pointer, drag, scroll, text, and key actions through the authorizer.
3. Replace cached-awareness keyboard checks with direct foreground checks.
4. Revalidate foreground physical actions at execution time.
5. Require and validate a permit in the physical executor.
6. Add independent protected-process checks to CUA and UI Automation.
7. Exclude ASHA from general application launch and window management.
8. Add bounded denial telemetry.
9. Add adversarial tests for attached/detached chat, focus changes, and every
   executor route.

## Acceptance criteria

Phase One is implemented when:

1. ASHA cannot click, double-click, right-click, drag, scroll, type, or send
   keys to any ASHA-owned window.
2. ASHA cannot invoke its own controls through UI Automation or CUA.
3. ASHA cannot launch, activate, minimize, maximize, restore, or close itself
   through general desktop tools.
4. Moving, resizing, or detaching the conversation does not weaken protection.
5. A keyboard action is rejected when ASHA is the real foreground process,
   even when awareness retains a previous non-ASHA foreground surface.
6. Physical text stops when foreground identity changes.
7. Human typing, pasting, sending, settings changes, and window movement remain
   functional.
8. Approved interactions with non-ASHA applications continue to pass existing
   grounding, lease, top-layer, and post-action verification tests.
9. Denials are auditable without retaining composer contents.

## Later work

Screenshot masking, Windows session-lock and RDP lease revocation, optional
remote-use mode, stronger local-presence approval, and general untrusted-screen
content policy are subsequent phases. They do not weaken or replace this
permanent self-target exclusion.
