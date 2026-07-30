# 009 — Searchable settings information architecture

**Status:** Planned

**Decision:** Accepted for implementation

**Last reviewed:** 2026-07-30

**Related:** [001 — Computer control and conversational permissions](001-computer-control-and-permissions.md), [006 — Durable and temporary session lifecycle](006-durable-and-temporary-session-lifecycle.md), [008 — Privacy-controlled online knowledge](008-privacy-controlled-online-knowledge.md)

## Decision

ASHA keeps the orb and its expanded panel small. Detailed configuration moves
into a dedicated, movable, resizable, searchable, and scrollable settings
window.

The redesign migrates existing settings. It does not remove, silently reset,
rename without compatibility, or change the meaning of working controls.

## Surface responsibilities

### Orb panel

The compact panel contains only frequent, current-state actions:

- listening and speaking state;
- current session and chat;
- computer-control lease state;
- concise privacy and service health;
- Settings, Hide to tray, and Quit.

### Full settings window

The settings window uses a fixed category sidebar and an independently
scrollable content pane. Dynamic status text has bounded space and cannot
resize unrelated controls.

## Categories

### Personal

- General
- Profiles
- Appearance
- Voice and language
- Sessions and memory
- Shortcuts

### Shared attention

- Desktop awareness
- Visual guidance
- Teaching and recording
- Projects and channels

### Capabilities

- Computer control
- Online knowledge
- Applications and folders
- Agent connections

### System

- AI providers
- Local speech services
- Privacy and data
- Diagnostics
- Experimental
- About ASHA

## Control semantics

- Switches represent genuine Boolean state only.
- Permission policy uses **Off**, **Ask**, or **Available**, not several
  contradictory switches.
- Provider and executor choices use selectors.
- Child settings remain visible but subdued when their parent capability is
  off, with a short explanation.
- Every setting declares whether it is Global, Profile, Project, or Session
  scoped.
- Risky capabilities use calm, specific explanations and show their effective
  state.
- Search returns settings by human-facing name, description, and relevant
  synonyms.

## Migration

1. Define a versioned settings schema independent of WPF controls.
2. Map every existing preference to one destination category and scope.
3. Load old preferences through a non-destructive migration.
4. Keep the existing compact panel functional while the full window is built.
5. Add accessibility names, keyboard navigation, high-DPI behavior, and
   bounded layout tests.
6. Remove a legacy panel control only after its replacement is live and
   verified.

## Acceptance criteria

1. Every current setting has one documented destination and retains its value
   after upgrade.
2. The settings window works at different Windows scale factors and display
   sizes without clipped controls.
3. The category sidebar remains visible while long pages scroll.
4. Search can locate computer control, speech vocabulary, online knowledge,
   session retention, and provider configuration.
5. Global and profile-derived values are visibly distinguishable.
6. Closing Settings does not hide or stop the ASHA orb.
