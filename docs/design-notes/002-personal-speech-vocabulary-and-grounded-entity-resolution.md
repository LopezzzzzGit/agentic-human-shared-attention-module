# Design note 002 — Personal speech vocabulary and grounded entity resolution

**Status:** In progress
**Related:** [001 — Computer control and conversational permissions](001-computer-control-and-permissions.md)

## Decision

ASHA will treat imperfect speech recognition as uncertain evidence rather than
as an exact command string. A generic resolution layer will combine:

1. local contextual speech biasing;
2. an explicitly taught personal vocabulary;
3. current UI Automation, OCR, window, session, and project evidence;
4. a confidence-and-margin policy that either resolves, asks, or declines.

No person's words, applications, accounts, workflows, or corrections are
compiled into ASHA. Personal vocabulary is local data outside the repository.

## Terminology

- **Personal speech vocabulary** — canonical spellings and user-confirmed
  spoken aliases.
- **Contextual biasing** — a bounded set of relevant terms supplied to the
  speech recognizer for the current turn.
- **Grounded entity resolution** — matching an imperfect reference against
  independently available candidates such as visible controls or OCR text.
- **Clarification policy** — deciding whether ASHA may use a unique candidate,
  must ask “Do you mean this?”, or must gather more evidence.

This is not model fine-tuning and not speaker-voice training.

## Vocabulary data model

Each entry contains:

- stable entry ID;
- canonical spelling;
- zero or more explicitly confirmed spoken aliases;
- scope: global, profile, project, or session;
- optional scope ID;
- creation and last-confirmation timestamps.

The default store is:

`%LOCALAPPDATA%\ASHA\speech-vocabulary.json`

The file is never committed. ASHA writes it atomically and applies bounded
entry, alias, term-length, and prompt-size limits.

## Context selection

ASHA does not send the complete vocabulary on every turn. It selects a small
set in this priority order:

1. active session;
2. active project;
3. active profile;
4. global vocabulary;
5. recently confirmed or recently used terms within those scopes.

Only the bounded selected set is passed to the local speech endpoint. If a
future non-local speech provider is configured, transmitting personal
vocabulary requires its own explicit privacy consent.

## Speech-recognition transport

The local `/stt` multipart contract gains two optional fields:

- `hotwords`;
- `initial_prompt`.

Both are passed to faster-whisper after server-side length normalization.
Older endpoints remain compatible because ASHA omits empty fields.

Contextual biasing reduces mistakes; it does not prove the resulting spelling.
The raw transcript remains evidence and is never silently rewritten by an
unconfirmed fuzzy match. The recognizer applies voice-activity filtering and
does not condition one turn on previous decoded text, because a prompt must
not turn silence into a taught word.

## Confirmed alias correction

An explicitly saved alias may replace a whole word or phrase in a transcript.
The timeline retains:

- raw transcript;
- resolved transcript;
- vocabulary entry ID and scope;
- whether the correction was automatic from a confirmed alias or confirmed
  conversationally during this turn.

ASHA must not learn an alias merely because a model guessed one.

## Grounded candidate resolution

Candidate sources may include:

- current UI Automation names, roles, ancestry, and state;
- local OCR text;
- foreground window and application identity;
- active session and project vocabulary;
- explicitly taught aliases.

Candidate scoring is generic and Unicode-aware. It may consider:

- canonical and exact token matches;
- confirmed aliases;
- normalized spelling distance;
- token coverage;
- requested semantic role and container;
- candidate visibility and currentness;
- distance from an intentional spatial hint;
- uniqueness relative to the runner-up.

Application-specific process aliases, when required for safe top-layer
verification, belong to an application-identity adapter—not to personal
speech vocabulary and not to a workflow recipe.

### Two-resolution visual evidence

The image sent to a vision provider remains compressed for responsiveness and
token economy. Text grounding uses a separate, lossless, higher-resolution
capture of the same desktop rectangle that stays in local process memory.
Both images share one explicit coordinate map. Local OCR bounds are mapped
back through the provider image into desktop coordinates before a cue or input
can be emitted.

The higher-resolution image is never added to the provider request merely
because it exists. A later tightly cropped provider view remains an explicit,
justified perception action.

### Runtime-owned snapshot identity

Snapshot IDs and signatures are runtime evidence, not model-authored
arguments. The model identifies a semantic target; ASHA attaches the current
snapshot internally, re-reads the exposed UI immediately before input, and
rejects the action if the signature changed. This prevents malformed,
fabricated, or stale model-supplied IDs from weakening the freshness check.

## Confidence policy

Resolution depends on both absolute confidence and separation from the next
candidate:

- **High and unique:** ASHA may use the grounded canonical candidate for a
  reversible, permitted action and should make the normalization visible.
- **Medium or weakly separated:** ASHA asks a short clarification and may
  highlight the proposed target.
- **Low:** ASHA gathers a broader view or explains that no grounded candidate
  was established.
- **Ambiguous:** ASHA presents the smallest useful distinction between the top
  candidates.

Sensitive, destructive, external, or difficult-to-reverse actions always use
stricter permission and confirmation rules regardless of match confidence.

## Conversational teaching

Under **Speech → Teach ASHA new words…**, the person can:

- add a canonical spelling;
- add or remove spoken aliases;
- choose scope;
- edit or delete an entry.

ASHA may also propose:

> “I heard one phrase, but the visible name is another. Should I remember that
> pronunciation?”

The confirmation card shows canonical spelling, alias, scope, and where the
proposal came from. Nothing is stored until the person confirms.

## Privacy and portability

- Vocabulary stays local by default.
- Repository tests use fabricated terms only.
- Logs contain bounded term-resolution evidence, not microphone audio.
- Export and import are explicit future actions.
- Deleting a profile, project, or session can delete its scoped vocabulary.
- A shared project must not automatically expose private profile vocabulary.

## Failure isolation

Speech resolution does not replace desktop grounding. Capture routing,
application identity, stale snapshots, top-layer verification, provider rate
limits, and action execution remain separate diagnosable stages.

## Implementation sequence

1. Add the local vocabulary model, store, scope filter, and bounded prompt
   builder.
2. Add confirmed-alias transcript normalization with raw/resolved telemetry.
3. Add the generic candidate resolver and confidence/margin tests.
4. Extend the local `/stt` endpoint with optional `hotwords` and
   `initial_prompt`.
5. Pass the bounded active vocabulary from ASHA to `/stt`.
6. Route uncertain visible-target matches to a clarification result instead of
   an unsafe click.
7. Add the settings editor and conversational confirmation card.
8. Add import, export, and deletion lifecycle controls.

## Acceptance criteria

1. A clean installation contains no personal vocabulary.
2. Personal entries are stored outside the repository.
3. Scope filtering prevents unrelated project or session terms from entering
   a turn.
4. Prompt generation is bounded and deterministic.
5. A confirmed alias can normalize a transcript while retaining the raw text.
6. One strong visible candidate can be proposed without an application recipe.
7. Similar competing candidates produce clarification rather than action.
8. Low-confidence candidates do not cause pointer or keyboard input.
9. The local STT endpoint remains compatible when no vocabulary is supplied.
10. Tests use generic fabricated terms and cover multilingual Unicode text.
11. Silent audio remains an empty transcript even when vocabulary hints are
    supplied.
12. Provider-image compression does not reduce the resolution used for local
    OCR grounding.
13. A model tool call cannot supply or alter the runtime snapshot identity.
