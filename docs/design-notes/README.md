# ASHA design notes

Design notes are essential development documents for ASHA. They preserve
product and architecture decisions that are too detailed for the roadmap and
must remain readable after implementation.

Unless a note explicitly says otherwise, treat it as actionable work:

- **Proposed** means the design is still being evaluated.
- **Planned** means the design is accepted and intended for implementation.
- **In progress** means implementation has started but the acceptance criteria
  are not complete.
- **Implemented** means the design is present and verified. The note remains
  part of ASHA's permanent design history.
- **Deferred** means the design remains valid but is not currently scheduled.
- **Rejected** means it was considered and deliberately not selected.
- **Superseded by NNN** means a later numbered note changed the decision. The
  old note remains in place and links to its replacement.

## Permanent numbering rules

1. Filenames use `NNN-short-descriptive-name.md`, beginning with `001`.
2. Allocate the next integer after the highest number already present.
3. Never reuse a number, including after rejection, deletion, or supersession.
4. Never renumber an existing note.
5. Gaps remain gaps.
6. Every note states its number in the title and has an explicit status.
7. A replacement receives a new number and links back to the earlier note.

The number identifies the document only. Status communicates whether it is a
proposal, planned work, implemented design, or retained historical decision.

## Design-note register

| Number | Design note | Status |
| --- | --- | --- |
| 001 | [Computer control and conversational permissions](001-computer-control-and-permissions.md) | Planned |
| 002 | [Personal speech vocabulary and grounded entity resolution](002-personal-speech-vocabulary-and-grounded-entity-resolution.md) | In progress |
| 003 | [Protected control plane and self-target exclusion](003-protected-control-plane-and-self-target-exclusion.md) | Implemented; live acceptance pending |
| 004 | [Trusted observation and desktop-session boundaries](004-trusted-observation-and-desktop-session-boundaries.md) | Implemented; live acceptance pending |
| 005 | [Approval transactions and emergency stop](005-approval-transactions-and-emergency-stop.md) | Implemented; live acceptance pending |
| 006 | [Durable and temporary session lifecycle](006-durable-and-temporary-session-lifecycle.md) | Implemented; live acceptance pending |
| 007 | [Portable grounded-interaction architecture](007-portable-grounded-interaction-architecture.md) | Planned |
| 008 | [Privacy-controlled online knowledge](008-privacy-controlled-online-knowledge.md) | Planned |
| 009 | [Searchable settings information architecture](009-settings-information-architecture.md) | Planned |
