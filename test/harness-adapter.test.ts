import assert from "node:assert/strict";
import test from "node:test";
import { ashaToolManifest, dispatchAshaTool } from "../src/harness-adapter.ts";
import type { DriverClient } from "../src/mark-engine.ts";

test("the manifest exposes the shared-attention tools without a model dependency", () => {
  assert.deepEqual(
    ashaToolManifest.map((tool) => tool.name),
    ["asha_mark", "asha_ack", "asha_move", "asha_clear", "asha_armed"],
  );
  assert.equal(ashaToolManifest[0].inputSchema.required instanceof Array, true);
});

test("a normalized harness call dispatches to the mark engine", async () => {
  const calls: string[] = [];
  const driver: DriverClient = {
    async ensureReady() {},
    async call(tool) { calls.push(tool); },
  };
  const fakeMarks = {
    async mark() { return "mark-local"; },
    async acknowledge() {},
    async clear() {},
    async armed() {},
  };
  const result = await dispatchAshaTool(
    { name: "asha_mark", arguments: { kind: "dot", x: 10, y: 20 } },
    fakeMarks as never,
  );
  assert.deepEqual(result, { id: "mark-local" });
  assert.deepEqual(calls, []);
  void driver;
});
