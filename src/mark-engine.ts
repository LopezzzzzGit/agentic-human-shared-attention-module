import { execFile as execFileCallback, spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdir, readFile, rename, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const execFile = promisify(execFileCallback);
const SESSION = "asha";
const ACKNOWLEDGED_COLOR = "#22cc66";

export type MarkKind = "circle" | "box" | "dot" | "label" | "arrow" | "frame";

export interface MarkRequest {
  kind: MarkKind;
  x: number;
  y: number;
  w?: number;
  h?: number;
  label?: string;
  color?: string;
  id?: string;
}

export interface MarkUpdate {
  kind?: MarkKind;
  x?: number;
  y?: number;
  w?: number | null;
  h?: number | null;
  label?: string | null;
  color?: string;
}

export interface StoredMark extends MarkRequest {
  id: string;
  cursorId: string;
  spritePath: string;
  overlayPid?: number;
}

interface MarkState {
  nextId: number;
  marks: Record<string, StoredMark>;
  armed?: StoredMark;
  editing?: boolean;
}

export interface DriverClient {
  ensureReady(): Promise<void>;
  call(tool: string, args: Record<string, unknown>): Promise<void>;
}

export interface MarkRenderer {
  ensureReady(): Promise<void>;
  show(mark: StoredMark, color: string, editable: boolean): Promise<void>;
  move(mark: StoredMark, color: string, editable: boolean): Promise<void>;
  hide(mark: StoredMark): Promise<void>;
  clearAll(): Promise<void>;
}

export interface MarkEngineOptions {
  driver?: DriverClient;
  renderer?: MarkRenderer;
  statePath?: string;
  runtimeDir?: string;
}

/** Fallback client for cua-driver when a native mark overlay is unavailable. */
export class CuaDriverClient implements DriverClient {
  readonly executable: string;

  constructor(executable = process.env.CUA_DRIVER_PATH ?? defaultDriverPath()) {
    this.executable = executable;
  }

  async ensureReady(): Promise<void> {
    if (await this.isRunning()) return;
    const daemon = spawn(this.executable, ["serve"], { detached: true, stdio: "ignore", windowsHide: true });
    await new Promise<void>((resolveLaunch, rejectLaunch) => {
      daemon.once("spawn", resolveLaunch);
      daemon.once("error", rejectLaunch);
    });
    daemon.unref();
    for (let attempt = 0; attempt < 20; attempt += 1) {
      await delay(250);
      if (await this.isRunning()) return;
    }
    throw new Error("cua-driver did not become ready within 5 seconds.");
  }

  async call(tool: string, args: Record<string, unknown>): Promise<void> {
    await execFile(this.executable, ["call", tool, JSON.stringify(args)], { windowsHide: true });
  }

  private async isRunning(): Promise<boolean> {
    try {
      await execFile(this.executable, ["status"], { windowsHide: true });
      return true;
    } catch {
      return false;
    }
  }
}

class CuaMarkRenderer implements MarkRenderer {
  readonly driver: DriverClient;
  constructor(driver: DriverClient) { this.driver = driver; }
  async ensureReady(): Promise<void> { await this.driver.ensureReady(); }
  async show(mark: StoredMark, color: string, _editable: boolean): Promise<void> {
    await this.driver.call("set_agent_cursor_motion", {
      cursor_id: mark.cursorId, cursor_icon: mark.spritePath, cursor_color: color,
      cursor_label: mark.label ?? "", cursor_size: cursorSize(mark), cursor_opacity: 1,
      arc_size: 0, glide_duration_ms: 50, idle_hide_ms: 0,
    });
    await this.driver.call("set_agent_cursor_enabled", { cursor_id: mark.cursorId, enabled: true });
    await this.driver.call("move_cursor", { x: mark.x, y: mark.y, cursor_id: mark.cursorId });
  }
  async move(mark: StoredMark, _color: string, _editable: boolean): Promise<void> {
    await this.driver.call("move_cursor", { x: mark.x, y: mark.y, cursor_id: mark.cursorId });
  }
  async hide(mark: StoredMark): Promise<void> {
    await this.driver.call("set_agent_cursor_enabled", { cursor_id: mark.cursorId, enabled: false });
  }
  async clearAll(): Promise<void> { await this.driver.call("end_session", { session: SESSION }); }
}

/** Windows-native, click-through marks. It is the reliable default on this machine. */
class WindowsOverlayRenderer implements MarkRenderer {
  readonly executable: string;
  constructor(executable = join(packageRoot, "overlay", "bin", "Release", "net8.0-windows", "asha-overlay.exe")) {
    this.executable = executable;
  }
  async ensureReady(): Promise<void> {
    if (!existsSync(this.executable)) {
      throw new Error("ASHA overlay is not built. Run: npm run build:overlay");
    }
  }
  async show(mark: StoredMark, color: string, editable: boolean): Promise<void> {
    await this.hide(mark);
    const request = JSON.stringify({
      id: mark.id, kind: mark.kind, x: mark.x, y: mark.y, w: mark.w, h: mark.h,
      label: mark.label, color, editable,
      eventDirectory: join(defaultRuntimeDir(), "cue-edit-events"),
    });
    const process = spawn(this.executable, [request], { detached: true, stdio: "ignore", windowsHide: true });
    await new Promise<void>((resolveLaunch, rejectLaunch) => {
      process.once("spawn", resolveLaunch);
      process.once("error", rejectLaunch);
    });
    mark.overlayPid = process.pid;
    process.unref();
  }
  async move(mark: StoredMark, color: string, editable: boolean): Promise<void> { await this.show(mark, color, editable); }
  async hide(mark: StoredMark): Promise<void> {
    if (!mark.overlayPid) return;
    try { process.kill(mark.overlayPid); } catch { /* already closed */ }
    delete mark.overlayPid;
  }
  async clearAll(): Promise<void> { }
}

/** Persistent mark state lets independent `asha` invocations manage the same marks. */
export class MarkEngine {
  private readonly renderer: MarkRenderer;
  private readonly statePath: string;
  private readonly runtimeDir: string;
  private state: MarkState | undefined;

  constructor(options: MarkEngineOptions = {}) {
    this.renderer = options.renderer ?? (options.driver ? new CuaMarkRenderer(options.driver) : defaultRenderer());
    this.runtimeDir = options.runtimeDir ?? defaultRuntimeDir();
    this.statePath = options.statePath ?? join(this.runtimeDir, "marks.json");
  }

  async mark(request: MarkRequest): Promise<string> {
    validateMark(request);
    await this.renderer.ensureReady();
    const state = await this.loadState();
    const id = request.id ?? `mark-${state.nextId++}`;
    validateId(id);
    if (state.marks[id]) throw new Error(`A mark named '${id}' already exists.`);
    const color = request.color ?? defaultColor(request.kind);
    const stored: StoredMark = {
      ...request, id, cursorId: id,
      spritePath: await this.writeSprite(id, request.kind, color, request),
    };
    await this.draw(stored, color, state.editing === true && stored.kind !== "frame");
    state.marks[id] = stored;
    await this.saveState();
    return id;
  }

  async acknowledge(id: string): Promise<void> {
    const state = await this.loadState();
    const mark = state.marks[id];
    if (!mark) throw new Error(`No active mark named '${id}'.`);
    await this.renderer.ensureReady();
    await this.draw(mark, ACKNOWLEDGED_COLOR, state.editing === true);
    await delay(1500);
    await this.clear(id);
  }

  async move(id: string, x: number, y: number): Promise<void> {
    if (!Number.isFinite(x) || !Number.isFinite(y)) throw new Error("Mark coordinates must be finite screen-pixel numbers.");
    const state = await this.loadState();
    const mark = state.marks[id];
    if (!mark) throw new Error(`No active mark named '${id}'.`);
    await this.renderer.ensureReady();
    mark.x = x;
    mark.y = y;
    await this.renderer.move(mark, mark.color ?? defaultColor(mark.kind), state.editing === true && mark.kind !== "frame");
    await this.saveState();
  }

  /** Update the editable properties of one persistent visual cue. */
  async update(id: string, patch: MarkUpdate): Promise<void> {
    const state = await this.loadState();
    const mark = state.marks[id];
    if (!mark) throw new Error(`No active mark named '${id}'.`);

    const next: MarkRequest = {
      kind: patch.kind ?? mark.kind,
      x: patch.x ?? mark.x,
      y: patch.y ?? mark.y,
      w: Object.hasOwn(patch, "w") ? patch.w ?? undefined : mark.w,
      h: Object.hasOwn(patch, "h") ? patch.h ?? undefined : mark.h,
      label: Object.hasOwn(patch, "label") ? patch.label ?? undefined : mark.label,
      color: patch.color ?? mark.color,
    };
    validateMark(next);
    await this.renderer.ensureReady();
    mark.kind = next.kind;
    mark.x = next.x;
    mark.y = next.y;
    mark.w = next.w;
    mark.h = next.h;
    mark.label = next.label;
    mark.color = next.color;
    await this.draw(mark, mark.color ?? defaultColor(mark.kind), state.editing === true && mark.kind !== "frame");
    await this.saveState();
  }

  async clear(id?: string): Promise<void> {
    const state = await this.loadState();
    await this.renderer.ensureReady();
    if (id) {
      const mark = state.marks[id];
      if (!mark) return;
      await this.renderer.hide(mark);
      delete state.marks[id];
      await this.saveState();
      return;
    }
    for (const mark of Object.values(state.marks)) await this.renderer.hide(mark);
    if (state.armed) await this.renderer.hide(state.armed);
    state.marks = {};
    delete state.armed;
    await this.renderer.clearAll();
    await this.saveState();
  }

  async armed(on: boolean): Promise<void> {
    const state = await this.loadState();
    await this.renderer.ensureReady();
    if (!on) {
      if (state.armed) await this.renderer.hide(state.armed);
      delete state.armed;
      await this.saveState();
      return;
    }
    if (state.armed) await this.renderer.hide(state.armed);
    const request: MarkRequest = { kind: "label", x: 38, y: 38, label: "TEACH MODE", color: "#ff3366" };
    const armed: StoredMark = {
      ...request, id: "asha-armed", cursorId: "asha-armed",
      spritePath: await this.writeSprite("asha-armed", request.kind, request.color!, request),
    };
    await this.draw(armed, request.color!, false);
    state.armed = armed;
    await this.saveState();
  }

  async activeMarkIds(): Promise<string[]> { return Object.keys((await this.loadState()).marks); }

  /**
   * Editing is deliberately a temporary mode.  Ordinary attention cues remain
   * click-through; while editing, a cue can receive a click, drag, or
   * right-click without activating the application behind it.
   */
  async setEditing(on: boolean): Promise<void> {
    const state = await this.loadState();
    await this.renderer.ensureReady();
    state.editing = on;
    for (const mark of Object.values(state.marks)) {
      await this.draw(mark, mark.color ?? defaultColor(mark.kind), on && mark.kind !== "frame");
    }
    await this.saveState();
  }

  private async draw(mark: StoredMark, color: string, editable: boolean): Promise<void> {
    mark.spritePath = await this.writeSprite(mark.id, mark.kind, color, mark);
    await this.renderer.show(mark, color, editable);
  }

  private async loadState(): Promise<MarkState> {
    if (this.state) return this.state;
    try {
      const parsed = JSON.parse(await readFile(this.statePath, "utf8")) as Partial<MarkState>;
      this.state = { nextId: Math.max(1, Number(parsed.nextId) || 1), marks: parsed.marks ?? {}, armed: parsed.armed, editing: parsed.editing === true };
    } catch (error: unknown) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
      this.state = { nextId: 1, marks: {} };
    }
    return this.state;
  }

  private async saveState(): Promise<void> {
    const state = await this.loadState();
    await mkdir(dirname(this.statePath), { recursive: true });
    const temporary = `${this.statePath}.tmp`;
    await writeFile(temporary, `${JSON.stringify(state, null, 2)}\n`, "utf8");
    await rename(temporary, this.statePath);
  }

  private async writeSprite(id: string, kind: MarkKind, color: string, mark: MarkRequest): Promise<string> {
    const directory = join(this.runtimeDir, "sprites");
    await mkdir(directory, { recursive: true });
    const path = join(directory, `${id}.svg`);
    await writeFile(path, spriteSvg(kind, color, mark), "utf8");
    return path;
  }
}

function defaultRenderer(): MarkRenderer {
  return process.platform === "win32" ? new WindowsOverlayRenderer() : new CuaMarkRenderer(new CuaDriverClient());
}

function defaultRuntimeDir(): string { return process.env.ASHA_RUNTIME_DIR ?? join(process.env.LOCALAPPDATA ?? process.cwd(), "asha"); }
function defaultDriverPath(): string {
  return process.platform === "win32" && process.env.LOCALAPPDATA
    ? join(process.env.LOCALAPPDATA, "Programs", "Cua", "cua-driver", "bin", "cua-driver.exe")
    : "cua-driver";
}
function defaultColor(kind: MarkKind): string {
  if (kind === "box") return "#3399ff";
  if (kind === "arrow") return "#766CFF";
  if (kind === "frame") return "#766CFF";
  return "#ffb020";
}
function cursorSize(mark: MarkRequest): number {
  if (mark.kind === "box") return Math.max(mark.w ?? 120, mark.h ?? 80) / 2;
  if (mark.kind === "dot" || mark.kind === "label") return 14;
  return Math.max(mark.w ?? 96, mark.h ?? 96) / 2;
}
function spriteSvg(kind: MarkKind, color: string, mark: MarkRequest): string {
  const safeColor = escapeXml(color);
  if (kind === "box") {
    const width = Math.max(12, mark.w ?? 120), height = Math.max(12, mark.h ?? 80);
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}"><rect x="3" y="3" width="${width - 6}" height="${height - 6}" rx="8" fill="none" stroke="${safeColor}" stroke-width="6" opacity="0.95"/></svg>`;
  }
  if (kind === "frame") {
    const width = Math.max(64, mark.w ?? 1920), height = Math.max(64, mark.h ?? 1080);
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}"><rect x="4" y="4" width="${width - 8}" height="${height - 8}" rx="18" fill="none" stroke="${safeColor}" stroke-width="8" opacity="0.92"/></svg>`;
  }
  if (kind === "arrow") {
    const width = Math.max(24, Math.abs(mark.w ?? 160));
    const height = Math.max(24, Math.abs(mark.h ?? 72));
    const canvasWidth = width + 20;
    const canvasHeight = height + 20;
    const startX = mark.w && mark.w < 0 ? canvasWidth - 10 : 10;
    const startY = mark.h && mark.h < 0 ? canvasHeight - 10 : 10;
    const endX = startX + (mark.w ?? 160);
    const endY = startY + (mark.h ?? 72);
    const angle = Math.atan2(endY - startY, endX - startX);
    const wing = (direction: number) => `${endX + 18 * Math.cos(angle + direction)},${endY + 18 * Math.sin(angle + direction)}`;
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${canvasWidth}" height="${canvasHeight}" viewBox="0 0 ${canvasWidth} ${canvasHeight}"><line x1="${startX}" y1="${startY}" x2="${endX}" y2="${endY}" stroke="${safeColor}" stroke-width="6" stroke-linecap="round"/><polyline points="${wing(Math.PI * 0.82)} ${endX},${endY} ${wing(-Math.PI * 0.82)}" fill="none" stroke="${safeColor}" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/></svg>`;
  }
  if (kind === "dot" || kind === "label") return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28"><circle cx="14" cy="14" r="9" fill="${safeColor}" stroke="#ffffff" stroke-width="3" opacity="0.95"/></svg>`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="96" height="96" viewBox="0 0 96 96"><circle cx="48" cy="48" r="42" fill="none" stroke="${safeColor}" stroke-width="6" opacity="0.95"/></svg>`;
}
function validateMark(mark: MarkRequest): void {
  if (!["circle", "box", "dot", "label", "arrow", "frame"].includes(mark.kind)) throw new Error(`Unsupported mark kind '${mark.kind}'.`);
  if (!Number.isFinite(mark.x) || !Number.isFinite(mark.y)) throw new Error("Mark coordinates must be finite screen-pixel numbers.");
  if (mark.kind === "box" && (!(Number.isFinite(mark.w)) || !(Number.isFinite(mark.h)) || mark.w! <= 0 || mark.h! <= 0)) throw new Error("A box mark requires positive finite w and h values.");
  if (mark.kind === "frame" && (!(Number.isFinite(mark.w)) || !(Number.isFinite(mark.h)) || mark.w! <= 0 || mark.h! <= 0)) throw new Error("A frame mark requires positive finite w and h values.");
  if (mark.kind === "arrow" && (!(Number.isFinite(mark.w)) || !(Number.isFinite(mark.h)) || (mark.w === 0 && mark.h === 0))) throw new Error("An arrow mark requires a non-zero finite w or h vector.");
  if (mark.id) validateId(mark.id);
}
function validateId(id: string): void {
  if (!/^[A-Za-z0-9._-]+$/.test(id)) throw new Error("Mark ids may contain only letters, digits, dots, underscores, and dashes.");
}
function escapeXml(value: string): string {
  return value.replace(/[&<>"']/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&apos;" })[character]!);
}
function delay(milliseconds: number): Promise<void> { return new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds)); }

export const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
