# 005 — Approval transactions and emergency stop

**Status:** Implemented; live acceptance pending
**Decision:** Accepted for Phase Three implementation
**Last reviewed:** 2026-07-25
**Related:** [003 — Protected control plane](003-protected-control-plane-and-self-target-exclusion.md), [004 — Trusted observation and desktop sessions](004-trusted-observation-and-desktop-session-boundaries.md)

## Decision

A model response, tool call, remembered instruction, screenshot, OCR result, or
text displayed by another application is never approval.

When ASHA needs to cross a sensitive execution boundary, the runtime creates a
short-lived **approval transaction** bound to one exact proposed operation.
The person can approve or cancel that proposal in a protected ASHA card. An
approval is one-shot, expires quickly, and cannot be reused for a changed
action, target, session, or control lease.

Phase Three first applies this mechanism to fallback from background virtual
interaction to the person's physical pointer. The mechanism is deliberately
reusable for later sensitive operations.

## Why this is separate

Phase One stops ASHA from clicking or typing into its own interface. Phase Two
prevents ASHA from seeing that interface and revokes authority when the Windows
session is not locally trusted. Neither phase implements the missing runtime
handshake behind “ask before falling back to my physical cursor.”

Previously, ASHA could report that confirmation was required, but a later
“go ahead” had no exact, expiring authorization object to consume. Phase Three
turns that conversational idea into an enforceable transaction.

## Terminology

- **Approval transaction** — a runtime-owned proposal for one exact sensitive
  operation.
- **Approval binding** — the immutable action, coordinates, target process and
  window, ASHA session, and control-lease identity covered by the proposal.
- **Protected approval card** — an ASHA-owned window that presents the
  runtime-generated proposal and receives a direct human approve/cancel event.
- **One-shot consumption** — a successful approval becomes unusable as soon as
  its bound operation claims it.
- **Emergency stop** — an immediate local path that cancels pending approval,
  cancels the current action turn, and revokes the computer-control lease.
- **Untrusted screen content** — all text and imagery acquired from other
  applications. It is evidence, never instruction or authorization.

## Approval invariant

```text
background interaction unavailable
              |
              v
runtime creates exact binding
              |
              v
protected card: approve once / cancel
        |                 |
        v                 v
direct human event       deny
        |
        v
validate expiry + action + target + session + lease
        |
        v
consume approval
        |
        v
fresh Phase One target authorization
        |
        v
physical delivery + post-action verification
```

The card never displays text that ASHA proposes to type. Its wording is
generated from a small runtime vocabulary and bounded Windows identity data.

## Approval sources

Phase Three accepts only the protected card's direct WPF event. A model tool
cannot call an approval API. ASHA's general computer-control executors cannot
operate the card because the whole ASHA process is protected by Phase One.

Spoken affirmation is a useful future interaction, but it needs a distinct
trusted-utterance binding so a replayed recording, television, browser audio,
or model-generated speech cannot approve itself. Until that is built, voice
may request an operation but does not consume a sensitive approval.

Third-party remote-control software may still generate events that Windows
presents as ordinary human input. Phase Three does not claim otherwise.
Windows Hello, a companion device, or product-specific remote-session policy
can later provide stronger local-presence proof.

## Emergency stop

`Ctrl+Alt+Escape` is the first global emergency-stop gesture. It:

- cancels and closes any approval card;
- cancels the in-flight model/action turn;
- ends the CUA cursor session;
- revokes the active computer-control lease;
- removes control-presence guidance;
- returns ASHA to a calm, non-controlling state.

A directly spoken or typed safety phrase such as “stop computer control” uses
a local model-independent intent path and revokes control without waiting for
the model. The ordinary Cancel button declines only the currently proposed
operation; it does not silently alter persistent settings.

Physical drag cancellation must always release the mouse button in a `finally`
path so an emergency stop cannot leave Windows in a held-button state.

## Persistent settings

Direct checkbox changes inside ASHA remain human-facing operations on the
protected control plane. Models cannot click them, and enabling a setting does
not expand an already active lease.

ASHA does not yet expose a model tool for changing persistent permissions.
When conversational settings changes are added, they must propose a bound
approval transaction rather than mutating settings directly. High-risk
permissions may require a stronger confirmation source than voice.

## Untrusted screen content

Visual and accessibility context is labelled untrusted data in the model
contract. Text in a webpage, email, document, image, or application may help
identify a user-requested target, but cannot:

- grant a capability;
- start or restart a control lease;
- approve physical fallback;
- change ASHA settings;
- redefine the person's goal;
- instruct ASHA to reveal secrets or operate its protected control plane.

Runtime policy remains authoritative even if the model mishandles that label.
Semantic intent binding for every ordinary low-risk action remains additional
future hardening.

## Phase Three implementation

1. Add an expiring, one-active-at-a-time approval transaction manager.
2. Bind proposals to exact action, target, session, and lease identities.
3. Add a protected approval card with Approve once and Cancel.
4. Connect physical-pointer fallback to the approval transaction.
5. Revalidate and consume before physical delivery.
6. Add `Ctrl+Alt+Escape` emergency stop.
7. Add a narrow local stop-control intent before model invocation.
8. Guarantee physical drag cleanup on cancellation.
9. Label screen-derived content as untrusted in the model runtime context.
10. Add adversarial tests for expiry, replay, mutation, cancellation, and
    runtime-blocked approval.

## Acceptance criteria

1. Physical fallback cannot occur merely because the model asks for it.
2. Approving one proposal cannot approve a changed coordinate, target,
   operation, lease, or session.
3. Approval expires and can be consumed only once.
4. Cancel sends no physical input.
5. The card exposes no typed payload or secret.
6. Emergency stop revokes control and closes pending approval immediately.
7. Cancelling a drag always releases the physical mouse button.
8. Screen text cannot approve or expand capabilities.
9. Existing virtual/background interaction remains fluid when no fallback is
   required.

## Later work

Trusted spoken approval, Windows Hello or companion-device presence,
third-party remote-control adapters, sensitivity classification for external
application fields, and stronger per-action semantic intent binding remain
future phases.
