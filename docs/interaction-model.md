# ASHA interaction model

ASHA is a **shared demonstration channel** between one person and one AI. It
is not a stamp-pad UI and it is not tied to a particular model, harness, or
agent framework.

## The normal surface: conversation presence

The normal on-screen surface is a small, borderless **conversation orb**. Its
blue-and-white cloud core sits behind a glass-like shell, with no title bar or
visible rectangular app window. Only the circle catches pointer input; the
surrounding transparent pixels pass through to the desktop. Drag the sphere to
move it; double-click, right-click, or use a hotkey to show the controls.

The larger playground remains an engineering and inspection surface, not the
primary interaction. The orb is a state-driven renderer: a voice bridge must
supply actual microphone or TTS energy before it is allowed to look as though
it is listening or speaking. A true desktop reflection will likewise use a
bounded, blurred screen-capture source rather than a painted fake reflection.

## One visual language, two actors

Both people and models use the same visual primitives. Every event records an
`actor` (`human` or `model`) and an `intent`.

| Primitive | Meaning | Example |
| --- | --- | --- |
| Attention dot | An exact target | “Do you mean this button?” |
| Region / drawn box | A meaningful area | “Zoom or capture this area.” |
| Arrow / gesture path | Direction or a drag | “Drag this handle to here.” |
| Optional label | Short semantic context | “Mailboxes” |

The human can use those primitives to teach. The model can use them in an
ordinary conversation, a guided tutorial, a clarification question, or before
an intended action.

## Visual pointer and computer control

The default agent pointer is a **virtual attention cursor**: a click-through
overlay drawn above the desktop. It can point, glide, highlight, and disappear,
but it never owns, moves, or clicks the person's physical mouse.

ASHA may also use a background-capable virtual interaction cursor or, under a
separate permission, the person's physical cursor. Action delivery, cursor
visibility, global capability policy, and the active session lease remain
separate decisions. The complete proposed settings, confirmation-card, CUA
routing, and emergency-stop design is in
[computer-control and conversational permission design](design-notes/001-computer-control-and-permissions.md).

## Real-time architecture

The fast desktop loop must never wait for an LLM:

1. **Local interaction layer** renders pointer motion and captures gestures.
2. **Telemetry layer** compresses movements into semantic pointer, region, and
   gesture events.
3. **Conversation coordinator** combines speech-to-text, model streaming,
   tool calls, and text-to-speech.
4. **Tool runtime** dispatches a model's normalized ASHA calls to the overlay
   and, only with approval, to any physical-control executor.

For natural voice, short-lived pointer motion stays local and ephemeral;
only completed gestures and meaningful semantic events are stored. This keeps
the desktop fluid while a slower reasoning model is still processing speech or
vision.

## Durable memory: ASHA projects and sessions

ASHA is not a forgetful floating assistant. Its durable hierarchy is:

```text
ASHA project
  └─ ASHA session (one coaching, tutorial, or work episode)
       ├─ harness links (for example Codex Remote project + thread)
       └─ semantic events: marked target, completed gesture, clarification,
          instruction, compiled recipe, approval, outcome
```

The harness retains its full conversation/transcript under its own policy.
ASHA stores a stable link to that session and the small, queryable shared-
attention record around it. Thus a future request such as “How did you teach
me the mailboxes?” can retrieve the ASHA session and follow its Codex link,
without pretending a 20 Hz audio meter is useful memory.

When ASHA itself coordinates the conversation, the ASHA session owns that
transcript instead of linking to an external copy. A session can begin as an
ephemeral working session and still keep short-term memory while running. On
ending, the person chooses Keep, Discard, or Cancel; “ephemeral” must never be
confused with forgetting between turns. The detailed lifecycle and delivery
order live in [the product roadmap](../ROADMAP.md).

Raw microphone data, complete cursor traces, and unbounded screen capture are
not ledger entries. They are transient local signals unless the person has
separately chosen a recording artifact with an explicit retention policy.

## First end-to-end slice

The first useful live test is deliberately small:

1. The person says, “Which button do you mean?”
2. The model calls `asha_mark` with a blue attention dot.
3. The model asks, “Do you mean this one?” through speech.
4. The person answers yes or no naturally.
5. ASHA acknowledges or clears the cue.

This proves shared attention, full-duplex conversation, and tool calling
without pretending that the model has unsupervised desktop control.
