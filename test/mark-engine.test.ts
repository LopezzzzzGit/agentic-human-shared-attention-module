import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { MarkEngine, type DriverClient } from "../src/mark-engine.ts";

class FakeDriver implements DriverClient {
  readonly calls: Array<{ tool: string; args: Record<string, unknown> }> = [];
  readyCount = 0;

  async ensureReady(): Promise<void> {
    this.readyCount += 1;
  }

  async call(tool: string, args: Record<string, unknown>): Promise<void> {
    this.calls.push({ tool, args });
  }
}

test("a mark is visible, stateful, and addressable by a later CLI process", async () => {
  const directory = await mkdtemp(join(tmpdir(), "asha-test-"));
  try {
    const driver = new FakeDriver();
    const statePath = join(directory, "marks.json");
    const first = new MarkEngine({ driver, statePath, runtimeDir: directory });
    const id = await first.mark({ kind: "box", x: 300, y: 200, w: 160, h: 80, label: "Save" });

    assert.equal(id, "mark-1");
    assert.equal(driver.readyCount, 1);
    assert.deepEqual(driver.calls.map((call) => call.tool), [
      "set_agent_cursor_motion",
      "set_agent_cursor_enabled",
      "move_cursor",
    ]);
    assert.equal(driver.calls.at(-1)?.args.cursor_id, "mark-1");

    const persisted = JSON.parse(await readFile(statePath, "utf8"));
    assert.ok(persisted.marks["mark-1"]);

    const laterDriver = new FakeDriver();
    const later = new MarkEngine({ driver: laterDriver, statePath, runtimeDir: directory });
    await later.clear("mark-1");
    assert.deepEqual(laterDriver.calls.map((call) => call.tool), ["set_agent_cursor_enabled"]);
    assert.deepEqual(await later.activeMarkIds(), []);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("invalid box dimensions are rejected before the driver is contacted", async () => {
  const directory = await mkdtemp(join(tmpdir(), "asha-test-"));
  try {
    const driver = new FakeDriver();
    const engine = new MarkEngine({ driver, runtimeDir: directory });
    await assert.rejects(
      engine.mark({ kind: "box", x: 10, y: 20, w: 0, h: 20 }),
      /positive finite w and h/,
    );
    assert.equal(driver.readyCount, 0);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("an arrow retains a signed start-to-tip vector", async () => {
  const directory = await mkdtemp(join(tmpdir(), "asha-test-"));
  try {
    const driver = new FakeDriver();
    const engine = new MarkEngine({ driver, runtimeDir: directory });
    await engine.mark({ kind: "arrow", x: 400, y: 300, w: -120, h: 64, label: "drag here" });
    const saved = JSON.parse(await readFile(join(directory, "marks.json"), "utf8"));
    assert.equal(saved.marks["mark-1"].kind, "arrow");
    assert.equal(saved.marks["mark-1"].w, -120);
    assert.equal(saved.marks["mark-1"].h, 64);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("renaming one cue does not rename other cues of the same shape", async () => {
  const directory = await mkdtemp(join(tmpdir(), "asha-test-"));
  try {
    const driver = new FakeDriver();
    const engine = new MarkEngine({ driver, runtimeDir: directory });
    const first = await engine.mark({ kind: "circle", x: 100, y: 100, label: "First" });
    const second = await engine.mark({ kind: "circle", x: 300, y: 100, label: "Second" });

    await engine.update(first, { label: "Renamed first" });

    const saved = JSON.parse(await readFile(join(directory, "marks.json"), "utf8"));
    assert.equal(saved.marks[first].label, "Renamed first");
    assert.equal(saved.marks[second].label, "Second");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("an active mark can move without being recreated", async () => {
  const directory = await mkdtemp(join(tmpdir(), "asha-test-"));
  try {
    const driver = new FakeDriver();
    const engine = new MarkEngine({ driver, runtimeDir: directory });
    const id = await engine.mark({ kind: "dot", x: 10, y: 20 });
    await engine.move(id, 300, 400);
    assert.deepEqual(driver.calls.at(-1), {
      tool: "move_cursor",
      args: { x: 300, y: 400, cursor_id: id },
    });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
