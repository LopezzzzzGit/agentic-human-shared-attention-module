import { readFile } from "node:fs/promises";

export interface RecordedEvent {
  t?: string;
  type: string;
  mode?: string;
  x?: number;
  y?: number;
  app?: string;
  pid?: number;
  window?: string;
  controlType?: string;
  name?: string;
  automationId?: string;
  rect?: number[];
  value?: string;
  delta?: number;
  seconds?: number;
  keys?: string;
}

export interface TargetAddress {
  by: "uia" | "text" | "relation" | "region" | "coordinate" | "window";
  value: unknown;
}

export interface RecipeStep {
  intent: string;
  targets: TargetAddress[];
  action: string;
  input?: string;
  expected: Record<string, unknown>;
}

export interface Recipe {
  title: string;
  steps: RecipeStep[];
}

/** JSON Schema a host-model adapter must return. Kept provider-neutral on purpose. */
export const recipeSchema = {
  type: "object",
  additionalProperties: false,
  required: ["title", "steps"],
  properties: {
    title: { type: "string" },
    steps: {
      type: "array",
      items: {
        type: "object",
        additionalProperties: false,
        required: ["intent", "targets", "action", "expected"],
        properties: {
          intent: { type: "string" },
          targets: { type: "array", minItems: 1 },
          action: { type: "string" },
          input: { type: "string" },
          expected: { type: "object" },
        },
      },
    },
  },
} as const;

/** Load the privacy-filtered JSONL written by teach-recorder. */
export async function readRecording(path: string): Promise<RecordedEvent[]> {
  return parseRecording(await readFile(path, "utf8"));
}

export function parseRecording(contents: string): RecordedEvent[] {
  return contents.split(/\r?\n/).filter(Boolean).map((line, index) => {
    let event: unknown;
    try {
      event = JSON.parse(line);
    } catch {
      throw new Error(`Recording line ${index + 1} is not valid JSON.`);
    }
    if (!event || typeof event !== "object" || typeof (event as RecordedEvent).type !== "string") {
      throw new Error(`Recording line ${index + 1} must contain a string type.`);
    }
    return event as RecordedEvent;
  });
}

/**
 * A deterministic, offline P0 compiler. It proves the complete recording →
 * recipe path and gives a local harness a valid fallback. A host can replace
 * this implementation with an LLM adapter while retaining `assertRecipe`.
 */
export function compileHeuristically(events: RecordedEvent[], title: string): Recipe {
  const steps = events
    .filter((event) => !["mode", "redacted", "dragstart"].includes(event.type))
    .map(toStep);
  const recipe = { title, steps };
  assertRecipe(recipe);
  return recipe;
}

/** Validate an LLM-produced recipe before it enters the host replay loop. */
export function assertRecipe(value: unknown): asserts value is Recipe {
  if (!value || typeof value !== "object") throw new Error("Recipe must be an object.");
  const recipe = value as Partial<Recipe>;
  if (typeof recipe.title !== "string" || !Array.isArray(recipe.steps)) {
    throw new Error("Recipe requires a string title and an array of steps.");
  }
  for (const [index, step] of recipe.steps.entries()) {
    if (!step || typeof step.intent !== "string" || typeof step.action !== "string" || !step.expected || typeof step.expected !== "object") {
      throw new Error(`Recipe step ${index + 1} is incomplete.`);
    }
    if (!Array.isArray(step.targets) || step.targets.length === 0) {
      throw new Error(`Recipe step ${index + 1} needs at least one target address.`);
    }
    if (step.targets[0].by === "coordinate" && step.targets.some((target) => target.by !== "coordinate")) {
      throw new Error(`Recipe step ${index + 1} puts coordinate evidence ahead of a semantic target.`);
    }
  }
}

/** Payload a harness can give to any local or cloud structured-output model. */
export function buildCompilerRequest(events: RecordedEvent[], title: string): Record<string, unknown> {
  return {
    title,
    recording: events,
    outputSchema: recipeSchema,
    requirements: [
      "Merge incidental low-level events into intentional steps.",
      "Order targets as UI Automation identity, visible text, relation, region, then coordinate evidence.",
      "Use raw coordinates only as the final fallback target.",
      "Describe an observable expected result for every step.",
    ],
  };
}

function toStep(event: RecordedEvent): RecipeStep {
  const targets = targetsFor(event);
  switch (event.type) {
    case "text":
      return {
        intent: `Enter text in ${event.name ?? "the focused field"}`,
        targets,
        action: "type",
        input: event.value ?? "",
        expected: { textPresent: event.value ?? "" },
      };
    case "dblclick":
      return actionStep(event, targets, "double_click", `Open ${event.name ?? "the selected item"}`);
    case "rightclick":
      return actionStep(event, targets, "right_click", `Open the context menu for ${event.name ?? "the target"}`);
    case "wheel":
      return {
        intent: "Scroll the current view",
        targets,
        action: "scroll",
        expected: { viewportChanged: true },
      };
    case "dragend":
      return actionStep(event, targets, "drag", `Drop on ${event.name ?? "the target"}`);
    case "hotkey":
      return {
        intent: `Use ${event.keys ?? "the recorded keyboard shortcut"}`,
        targets,
        action: "hotkey",
        input: event.keys,
        expected: { shortcutApplied: event.keys ?? true },
      };
    case "wait":
      return {
        intent: "Wait for the operation to complete",
        targets,
        action: "wait",
        expected: { waitedSeconds: event.seconds ?? 0 },
      };
    default:
      return actionStep(event, targets, "click", clickIntent(event));
  }
}

function actionStep(event: RecordedEvent, targets: TargetAddress[], action: string, intent: string): RecipeStep {
  return {
    intent,
    targets,
    action,
    expected: event.name ? { activated: event.name } : { actionCompleted: true },
  };
}

function clickIntent(event: RecordedEvent): string {
  if (event.controlType?.includes("MenuItem")) return `Open the ${event.name ?? "selected"} menu item`;
  return `Select ${event.name ?? "the target"}`;
}

function targetsFor(event: RecordedEvent): TargetAddress[] {
  const targets: TargetAddress[] = [];
  if (event.controlType || event.name || event.automationId) {
    targets.push({
      by: "uia",
      value: compact({ controlType: event.controlType, name: event.name, automationId: event.automationId }),
    });
  }
  if (event.name) targets.push({ by: "text", value: event.name });
  if (event.window) targets.push({ by: "window", value: event.window });
  if (event.rect && event.rect.length === 4) targets.push({ by: "region", value: event.rect });
  if (Number.isFinite(event.x) && Number.isFinite(event.y)) targets.push({ by: "coordinate", value: [event.x, event.y] });
  if (targets.length === 0) targets.push({ by: "window", value: "current active window" });
  return targets;
}

function compact(value: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined && item !== null && item !== ""));
}
