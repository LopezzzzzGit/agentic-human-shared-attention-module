import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { assertRecipe, compileHeuristically, parseRecording } from "../src/recipe-compiler.ts";

test("the demo notepad recording compiles to semantic, schema-valid steps", async () => {
  const fixture = await readFile(join(import.meta.dirname, "..", "fixtures", "demo-notepad.jsonl"), "utf8");
  const recipe = compileHeuristically(parseRecording(fixture), "Save meeting notes");

  assertRecipe(recipe);
  assert.ok(recipe.steps.length >= 3);
  assert.ok(recipe.steps.every((step) => step.targets[0].by !== "coordinate"));
  assert.equal(recipe.steps.find((step) => step.action === "type")?.input, "meeting-notes.txt");
});
