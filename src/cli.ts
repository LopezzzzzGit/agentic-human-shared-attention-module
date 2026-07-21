import { MarkEngine } from "./mark-engine.ts";
import { ashaToolManifest } from "./harness-adapter.ts";
import { existsSync } from "node:fs";
import { spawn } from "node:child_process";
import { join } from "node:path";
import { packageRoot } from "./mark-engine.ts";
import { compileHeuristically, readRecording } from "./recipe-compiler.ts";
import { SessionLedger, type HarnessLink, type SemanticEventInput } from "./session-ledger.ts";

export async function main(argv: string[]): Promise<void> {
  const [command, ...rest] = argv;
  const marks = new MarkEngine();

  try {
    switch (command) {
      case "mark": {
        const json = requireArgument(rest[0], "asha mark '<json>'");
        const id = await marks.mark(JSON.parse(json));
        emit({ id });
        return;
      }
      case "ack": {
        await marks.acknowledge(requireArgument(rest[0], "asha ack <id>"));
        emit({ ok: true });
        return;
      }
      case "move": {
        const id = requireArgument(rest[0], "asha move <id> '<json>'");
        const point = JSON.parse(requireArgument(rest[1], "asha move <id> '<json>'")) as { x?: unknown; y?: unknown };
        if (typeof point.x !== "number" || typeof point.y !== "number") throw new Error("Move coordinates require numeric x and y values.");
        await marks.move(id, point.x, point.y);
        emit({ ok: true });
        return;
      }
      case "update": {
        const id = requireArgument(rest[0], "asha update <id> '<json>'");
        const patch = JSON.parse(requireArgument(rest[1], "asha update <id> '<json>'"));
        if (!patch || typeof patch !== "object" || Array.isArray(patch)) throw new Error("Cue update requires a JSON object.");
        await marks.update(id, patch);
        emit({ ok: true });
        return;
      }
      case "clear": {
        await marks.clear(rest[0]);
        emit({ ok: true });
        return;
      }
      case "armed": {
        const value = requireArgument(rest[0], "asha armed on|off");
        if (value !== "on" && value !== "off") throw new Error("Usage: asha armed on|off");
        await marks.armed(value === "on");
        emit({ ok: true });
        return;
      }
      case "edit": {
        const value = requireArgument(rest[0], "asha edit on|off");
        if (value !== "on" && value !== "off") throw new Error("Usage: asha edit on|off");
        await marks.setEditing(value === "on");
        emit({ ok: true, editing: value === "on" });
        return;
      }
      case "manifest":
        emit({ version: 1, tools: ashaToolManifest });
        return;
      case "teach":
        if (rest[0] !== "start") throw new Error("Usage: asha teach start");
        await startTeachRecorder();
        return;
      case "compile":
        emit(await compileRecording(rest));
        return;
      case "project":
        emit(await manageProject(rest));
        return;
      case "session":
        emit(await manageSession(rest));
        return;
      default:
        throw new Error(usage());
    }
  } catch (error: unknown) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}

function emit(value: unknown): void {
  process.stdout.write(`${JSON.stringify(value)}\n`);
}

function requireArgument(value: string | undefined, usageText: string): string {
  if (!value) throw new Error(`Usage: ${usageText}`);
  return value;
}

function usage(): string {
  return [
    "Usage:",
    "  asha mark '<json>'",
    "  asha ack <id> | move <id> '<json>' | update <id> '<json>' | clear [id]",
    "  asha armed on|off | edit on|off",
    "  asha teach start",
    "  asha manifest",
    "  asha project create <title> [--id <id>] | asha project list",
    "  asha session start --project <project-id> --title <title> [--harness <name> --harness-project <id> --harness-session <id> --url <url>]",
    "  asha session link <session-id> --harness <name> [--harness-project <id> --harness-session <id> --url <url>]",
    "  asha session record <session-id> '<semantic-event-json>' | record-many <session-id> '<semantic-event-array-json>' | show <session-id> | find <words> [--project <id>] | close <session-id>",
  ].join("\n");
}

async function startTeachRecorder(): Promise<void> {
  const executable = join(packageRoot, "teach-recorder", "bin", "Release", "net8.0-windows", "asha-teach-recorder.exe");
  if (!existsSync(executable)) {
    throw new Error("Teach recorder is not built. Run: npm run build:recorder");
  }

  const child = spawn(executable, [], { stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  child.stdout.pipe(process.stdout);
  child.stderr.pipe(process.stderr);
  const exitCode = await new Promise<number | null>((resolveExit, reject) => {
    child.once("error", reject);
    child.once("close", resolveExit);
  });
  if (exitCode !== 0) throw new Error(`Teach recorder exited with code ${exitCode ?? "unknown"}.`);
}

async function compileRecording(args: string[]): Promise<Record<string, unknown>> {
  const file = requireArgument(args[0], "asha compile <file.jsonl> --title <title>");
  const titleFlag = args.indexOf("--title");
  if (titleFlag === -1) throw new Error("Usage: asha compile <file.jsonl> --title <title>");
  const title = requireArgument(args[titleFlag + 1], "asha compile <file.jsonl> --title <title>");
  return compileHeuristically(await readRecording(file), title);
}

async function manageProject(args: string[]): Promise<Record<string, unknown>> {
  const ledger = new SessionLedger();
  switch (args[0]) {
    case "create": {
      const title = requireArgument(args[1], "asha project create <title>");
      const id = option(args, "--id");
      return { project: await ledger.createProject(title, id) };
    }
    case "list":
      return { projects: await ledger.listProjects() };
    default:
      throw new Error("Usage: asha project create <title> [--id <id>] | asha project list");
  }
}

async function manageSession(args: string[]): Promise<Record<string, unknown>> {
  const ledger = new SessionLedger();
  switch (args[0]) {
    case "start": {
      const projectId = requireArgument(option(args, "--project"), "asha session start --project <project-id> --title <title>");
      const title = requireArgument(option(args, "--title"), "asha session start --project <project-id> --title <title>");
      const link = linkFromOptions(args);
      const id = option(args, "--id");
      return { session: await ledger.startSession({ projectId, title, id, links: link ? [link] : [] }) };
    }
    case "link": {
      const sessionId = requireArgument(args[1], "asha session link <session-id> --harness <name>");
      return { session: await ledger.addLink(sessionId, requireLink(args)) };
    }
    case "record": {
      const sessionId = requireArgument(args[1], "asha session record <session-id> '<semantic-event-json>'");
      const rawEvent = requireArgument(args[2], "asha session record <session-id> '<semantic-event-json>'");
      return { event: await ledger.record(sessionId, JSON.parse(rawEvent) as SemanticEventInput) };
    }
    case "record-many": {
      const sessionId = requireArgument(args[1], "asha session record-many <session-id> '<semantic-event-array-json>'");
      const rawEvents = requireArgument(args[2], "asha session record-many <session-id> '<semantic-event-array-json>'");
      const parsed = JSON.parse(rawEvents);
      if (!Array.isArray(parsed)) throw new Error("record-many needs a JSON array of semantic events.");
      return { events: await ledger.recordMany(sessionId, parsed as SemanticEventInput[]) };
    }
    case "show":
      return await ledger.readSession(requireArgument(args[1], "asha session show <session-id>"));
    case "find": {
      const query = requireArgument(args[1], "asha session find <words> [--project <id>]");
      return { matches: await ledger.search(query, option(args, "--project")) };
    }
    case "close":
      return { session: await ledger.closeSession(requireArgument(args[1], "asha session close <session-id>")) };
    default:
      throw new Error("Usage: asha session start|link|record|show|find|close …");
  }
}

function requireLink(args: string[]): HarnessLink {
  const link = linkFromOptions(args);
  if (!link) throw new Error("A harness link needs --harness <name>.");
  return link;
}

function linkFromOptions(args: string[]): HarnessLink | undefined {
  const harness = option(args, "--harness");
  if (!harness) return undefined;
  const projectId = option(args, "--harness-project");
  const sessionId = option(args, "--harness-session");
  const url = option(args, "--url");
  return { harness, projectId, sessionId, url };
}

function option(args: string[], name: string): string | undefined {
  const index = args.indexOf(name);
  if (index === -1) return undefined;
  return args[index + 1];
}
