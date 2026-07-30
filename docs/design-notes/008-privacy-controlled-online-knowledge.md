# 008 — Privacy-controlled online knowledge

**Status:** Planned

**Decision:** Accepted for implementation

**Last reviewed:** 2026-07-30

**Related:** [004 — Trusted observation and desktop-session boundaries](004-trusted-observation-and-desktop-session-boundaries.md), [007 — Portable grounded-interaction architecture](007-portable-grounded-interaction-architecture.md)

## Decision

ASHA may consult external information only through an explicit **Online
knowledge** capability. Search is a provider-neutral adapter, not a hidden
property of the conversation model.

The initial policy choices are:

- **Off**
- **Ask before researching**
- **Available in this profile or session**

Availability never authorizes disclosure of arbitrary conversation, screen,
project, or personal context.

## Provider interface

The provider interface accepts a minimized query and returns source metadata,
short result extracts, and stable links. Implementations may include:

- a local or self-hosted SearXNG service;
- a user-configured commercial search API;
- an organizational search gateway; or
- another platform-specific provider.

ASHA does not require Pete's server, credentials, or one public search engine.
Provider choice belongs in settings and may be global or profile-scoped.

## Privacy pipeline

Before a query leaves the computer:

1. derive the smallest useful query locally;
2. remove secrets, credentials, addresses, account identifiers, local paths,
   unrelated screen text, and unnecessary personal names;
3. classify the remaining disclosure risk;
4. ask for confirmation when sensitive information remains;
5. record the exact outbound query and selected provider in the local session
   ledger; and
6. send no screenshot, raw audio, or full conversation history by default.

The person can inspect what was shared. Retention of research history is a
separate setting.

## Trust boundary

Search results are untrusted evidence. Their text cannot approve an action,
change a permission, introduce a tool, or override ASHA's instructions.
Results remain quoted or attributed to their source and must be corroborated
when accuracy or safety requires it.

ASHA distinguishes:

- current desktop observation;
- platform metadata;
- verified action result;
- information supplied by the person;
- online source evidence; and
- uncertain model inference.

## Acceptance criteria

1. Online knowledge defaults to Off on a clean installation.
2. A search can be completed through either a local test provider or a remote
   provider without changing the conversation runtime.
3. The outbound payload contains only the approved minimized query.
4. Potential secrets and local paths cause local refusal or confirmation
   before any network request.
5. The ledger records provider, query, sources, and consent without recording
   provider credentials.
6. Search content cannot trigger desktop tools or permission changes.
