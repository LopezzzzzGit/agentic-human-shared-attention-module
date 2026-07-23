import { MarkEngine, type MarkRequest } from "./mark-engine.ts";

export interface ToolManifestEntry {
  name: string;
  description: string;
  inputSchema: Record<string, unknown>;
}

export interface ToolCall {
  name: string;
  arguments: Record<string, unknown>;
}

/**
 * Provider-neutral capability description. A harness maps this to whatever
 * tool-call wire format its local model expects (OpenAI-compatible, llama.cpp,
 * vLLM, LM Studio, or a model-specific adapter).
 */
export const ashaToolManifest: ToolManifestEntry[] = [
  {
    name: "asha_mark",
    description: "Draw a persistent visual mark over a resolved desktop target before acting.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["kind", "x", "y"],
      properties: {
        kind: { type: "string", enum: ["circle", "box", "dot", "label", "arrow", "frame"] },
        x: { type: "number", description: "Target center x in screen pixels; for an arrow, its start x." },
        y: { type: "number", description: "Target center y in screen pixels; for an arrow, its start y." },
        w: { type: "number", description: "Box width; for an arrow, signed x distance from start to tip." },
        h: { type: "number", description: "Box height; for an arrow, signed y distance from start to tip." },
        label: { type: "string", description: "Short human-readable target label." },
        color: { type: "string", description: "CSS or hex mark color." },
        id: { type: "string", description: "Optional caller-chosen stable mark id." },
      },
    },
  },
  {
    name: "asha_ack",
    description: "Confirm a previously shown target: turn it green, then fade it away.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["id"],
      properties: { id: { type: "string" } },
    },
  },
  {
    name: "asha_move",
    description: "Move an existing visual mark to a new screen position without creating a second mark.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["id", "x", "y"],
      properties: {
        id: { type: "string" },
        x: { type: "number", description: "New screen x in pixels." },
        y: { type: "number", description: "New screen y in pixels." },
      },
    },
  },
  {
    name: "asha_clear",
    description: "Remove one active visual mark, or all ASHA marks when id is omitted.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      properties: { id: { type: "string" } },
    },
  },
  {
    name: "asha_armed",
    description: "Show or hide the visible teach-mode indicator before a human demonstration.",
    inputSchema: {
      type: "object",
      additionalProperties: false,
      required: ["on"],
      properties: { on: { type: "boolean" } },
    },
  },
];

/** Execute one normalized tool call from an agentic host. */
export async function dispatchAshaTool(call: ToolCall, marks = new MarkEngine()): Promise<Record<string, unknown>> {
  switch (call.name) {
    case "asha_mark":
      return { id: await marks.mark(call.arguments as MarkRequest) };
    case "asha_ack":
      await marks.acknowledge(requireString(call.arguments.id, "id"));
      return { ok: true };
    case "asha_move":
      if (typeof call.arguments.x !== "number" || typeof call.arguments.y !== "number") {
        throw new Error("x and y must be numbers.");
      }
      await marks.move(requireString(call.arguments.id, "id"), call.arguments.x, call.arguments.y);
      return { ok: true };
    case "asha_clear": {
      const id = call.arguments.id;
      if (id !== undefined && typeof id !== "string") throw new Error("id must be a string when supplied.");
      await marks.clear(id);
      return { ok: true };
    }
    case "asha_armed":
      if (typeof call.arguments.on !== "boolean") throw new Error("on must be a boolean.");
      await marks.armed(call.arguments.on);
      return { ok: true };
    default:
      throw new Error(`Unknown ASHA tool '${call.name}'.`);
  }
}

function requireString(value: unknown, name: string): string {
  if (typeof value !== "string" || !value) throw new Error(`${name} must be a non-empty string.`);
  return value;
}
