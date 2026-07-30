# 007 — Portable grounded-interaction architecture

**Status:** Planned

**Decision:** Accepted for implementation

**Last reviewed:** 2026-07-30

**Related:** [001 — Computer control and conversational permissions](001-computer-control-and-permissions.md), [003 — Protected control plane and self-target exclusion](003-protected-control-plane-and-self-target-exclusion.md), [004 — Trusted observation and desktop-session boundaries](004-trusted-observation-and-desktop-session-boundaries.md)

## Decision

ASHA's reasoning contracts, evidence model, semantic targets, permissions,
session memory, and verification remain independent of a particular computer,
application, monitor layout, operating system, or model provider.

Operating-system functionality lives behind capability adapters. Windows is
the first implementation; a future macOS implementation must be possible
without replacing ASHA's product and safety core.

## Core primitives

ASHA uses four portable primitives:

1. **Surface resolver** — resolves a human description through the hierarchy
   desktop → application window → tab or document → panel → control or visual
   object.
2. **Evidence planner** — chooses the smallest sufficient evidence source:
   retained verified state, accessibility metadata, OCR, low-resolution
   overview, or targeted detail crop.
3. **Interaction executor** — delivers one semantic action using the
   explicitly selected adapter and never silently changes to a more intrusive
   executor.
4. **Outcome verifier** — compares before and after evidence and distinguishes
   target grounded, cue rendered, input delivered, visible change, verified
   outcome, and no response.

Model output proposes semantic intent. Runtime-owned adapters resolve,
authorize, execute, and verify it.

## Platform adapters

The current Windows adapter may use:

- Windows UI Automation;
- native window and monitor enumeration;
- Windows foreground activation;
- local OCR and screen capture;
- the CUA virtual interaction driver; and
- separately permissioned physical input.

A future macOS adapter may use Accessibility APIs, Quartz input events, and
ScreenCaptureKit. Those implementation choices must not appear in the
provider-neutral action schema or retained semantic recipe.

## Coordinate doctrine

- Never assume a fixed display resolution, scale factor, origin, or monitor
  count.
- Every coordinate carries its coordinate space and source-evidence identity.
- Provider-image coordinates normalize through the captured region into the
  current desktop or target surface.
- Semantic anchors, relative geometry, accessible identity, and verified
  relationships outrank raw coordinates.
- A stale image, changed monitor topology, moved window, or changed top layer
  invalidates dependent coordinates.

## Application independence

Application names, account names, filenames, browser brands, and individual
screen arrangements are runtime evidence, not command implementations.

Examples such as Outlook, Chrome, LM Studio, or a particular mailbox may be
used in tests. The production resolver must derive the same behavior from
current platform metadata and visual evidence on an unfamiliar computer.

## Annotation and correction

Non-text guidance follows:

`overview → target region → detail crop → grounded geometry → mark or decline`

An explicit correction refers to the previous cue and target. It moves,
replaces, or removes that cue instead of accumulating unrelated guesses.
Plural requests carry target cardinality and may produce multiple cue
geometries.

Rendering a cue proves only that rendering succeeded. It does not prove that
the requested object was located.

## Acceptance criteria

1. A descriptive target can resolve through a window and tab hierarchy without
   treating the description as a literal window title.
2. The same semantic action contract can be implemented by Windows and a mock
   non-Windows adapter.
3. No automated test depends on Pete's resolution, account names, directories,
   or installed applications.
4. Explicit virtual, background, physical, and demonstration execution choices
   remain distinguishable through delivery and verification.
5. A small unlabeled target is either grounded from a detail crop or declined;
   ASHA never reports a rendered guess as a verified target.
