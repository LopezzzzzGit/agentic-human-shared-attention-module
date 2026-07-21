import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { SessionLedger } from "../src/session-ledger.ts";

test("ASHA projects retain semantic teaching sessions and harness links", async () => {
  const directory = await mkdtemp(join(tmpdir(), "asha-ledger-test-"));
  try {
    const ledger = new SessionLedger({ rootDir: directory, now: () => new Date("2026-07-20T16:00:00.000Z") });
    const project = await ledger.createProject("Outlook coaching", "outlook");
    const session = await ledger.startSession({
      id: "teach-mailboxes",
      projectId: project.id,
      title: "Show the mailbox list",
      links: [{ harness: "codex-remote", projectId: "codex-project", sessionId: "codex-thread" }],
    });
    await ledger.record(session.id, {
      actor: "human",
      type: "cue.created",
      intent: "Identify the mailbox list",
      note: "These are the mailboxes.",
      content: "This is where the mailboxes are.",
      target: { app: "Outlook", label: "Mailboxes", x: 114, y: 168 },
      cue: { id: "cue-mailboxes", kind: "circle", x: 114, y: 168, w: 180, h: 120, label: "Mailboxes", color: "#4C8DFF" },
    });
    const saved = await ledger.readSession(session.id);
    assert.equal(saved.session.links[0].sessionId, "codex-thread");
    assert.equal(saved.events[0].target?.label, "Mailboxes");
    assert.equal(saved.events[0].content, "This is where the mailboxes are.");
    assert.equal(saved.events[0].cue?.id, "cue-mailboxes");
    const matches = await ledger.search("mailboxes");
    assert.equal(matches.length, 1);
    assert.equal(matches[0].session.id, session.id);
    assert.equal(matches[0].matchingEvents[0].actor, "human");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
