import { MarkEngine } from "../src/mark-engine.ts";

const marks = new MarkEngine();
const holdMilliseconds = process.argv.includes("--quick") ? 3_000 : 60_000;

const box = await marks.mark({
  kind: "box",
  x: 420,
  y: 280,
  w: 260,
  h: 120,
  label: "Save target",
});
await marks.mark({ kind: "dot", x: 740, y: 410, label: "Pointer" });
const circle = await marks.mark({ kind: "circle", x: 1020, y: 580, label: "Confirm" });

process.stderr.write(`Showing ${box}, a dot, and ${circle} for ${holdMilliseconds / 1000}s.\n`);
await new Promise((resolveDelay) => setTimeout(resolveDelay, holdMilliseconds));
await marks.acknowledge(circle);
await marks.clear();
