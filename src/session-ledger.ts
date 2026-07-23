import { appendFile, mkdir, readFile, rename, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { randomUUID } from "node:crypto";

/**
 * Persistent, provider-neutral memory for shared-attention work.
 *
 * The ledger deliberately stores completed semantic acts, not microphone
 * samples, unbounded cursor paths, or copied chat transcripts.  A harness link
 * points back to the conversation that owns those larger/private materials.
 */
export interface AshaProject {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
}

export interface HarnessLink {
  harness: string;
  projectId?: string;
  sessionId?: string;
  url?: string;
}

export interface AshaSession {
  id: string;
  projectId: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  closedAt?: string;
  links: HarnessLink[];
}

export interface SemanticTarget {
  app?: string;
  label?: string;
  control?: string;
  x?: number;
  y?: number;
  w?: number;
  h?: number;
}

/**
 * The durable state of a visual cue at one moment in a teaching session.
 * Keeping this beside the semantic event lets a later review reconstruct the
 * final lesson even when the person renamed, moved, or removed a cue.
 */
export interface CueState {
  id: string;
  kind: string;
  x: number;
  y: number;
  w?: number;
  h?: number;
  label?: string;
  color?: string;
}

/**
 * A local visual-evidence bundle. Files are relative to the private ASHA
 * runtime directory so a project ledger never needs to contain desktop images
 * or an absolute user path.
 */
export interface VisualEvidence {
  privacy: "local";
  beforeFile?: string;
  afterFile?: string;
  contextFile?: string;
  changedScore?: number;
  contextX?: number;
  contextY?: number;
  contextWidth?: number;
  contextHeight?: number;
}

export interface SemanticEventInput {
  actor: "human" | "model" | "system";
  type: string;
  intent?: string;
  note?: string;
  /** The locally retained words for a conversation event. Never sent by the ledger. */
  content?: string;
  target?: SemanticTarget;
  cue?: CueState;
  evidence?: VisualEvidence;
  reference?: HarnessLink;
}

export interface SemanticEvent extends SemanticEventInput {
  id: string;
  at: string;
}

export interface SessionView {
  session: AshaSession;
  events: SemanticEvent[];
}

export interface SessionSearchResult {
  project: AshaProject;
  session: AshaSession;
  matchingEvents: SemanticEvent[];
}

export interface SessionLedgerOptions {
  rootDir?: string;
  now?: () => Date;
}

export class SessionLedger {
  private readonly rootDir: string;
  private readonly now: () => Date;

  constructor(options: SessionLedgerOptions = {}) {
    this.rootDir = options.rootDir ?? join(defaultRuntimeDir(), "ledger");
    this.now = options.now ?? (() => new Date());
  }

  async createProject(title: string, id = makeId("project")): Promise<AshaProject> {
    const safeTitle = requiredText(title, "Project title");
    validateId(id);
    const projects = await this.readProjects();
    if (projects.some((project) => project.id === id)) throw new Error(`A project named '${id}' already exists.`);
    const now = this.timestamp();
    const project: AshaProject = { id, title: safeTitle, createdAt: now, updatedAt: now };
    projects.push(project);
    await this.writeProjects(projects);
    return project;
  }

  async listProjects(): Promise<AshaProject[]> {
    return [...await this.readProjects()].sort((left, right) => right.updatedAt.localeCompare(left.updatedAt));
  }

  async startSession(input: { projectId: string; title: string; links?: HarnessLink[]; id?: string }): Promise<AshaSession> {
    const projectId = requiredText(input.projectId, "Project id");
    const project = (await this.readProjects()).find((item) => item.id === projectId);
    if (!project) throw new Error(`No ASHA project named '${projectId}'.`);
    const id = input.id ?? makeId("session");
    validateId(id);
    const sessions = await this.readSessions();
    if (sessions.some((session) => session.id === id)) throw new Error(`An ASHA session named '${id}' already exists.`);
    const now = this.timestamp();
    const session: AshaSession = {
      id,
      projectId,
      title: requiredText(input.title, "Session title"),
      createdAt: now,
      updatedAt: now,
      links: (input.links ?? []).map(normalizeLink),
    };
    sessions.push(session);
    project.updatedAt = now;
    await Promise.all([this.writeSessions(sessions), this.writeProjects(await this.replaceProject(project))]);
    return session;
  }

  async listSessions(projectId?: string): Promise<AshaSession[]> {
    const sessions = await this.readSessions();
    return sessions
      .filter((session) => !projectId || session.projectId === projectId)
      .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
      .map((session) => structuredClone(session));
  }

  async resumeSession(sessionId: string): Promise<AshaSession> {
    const session = await this.requireSession(sessionId);
    if (session.closedAt) delete session.closedAt;
    session.updatedAt = this.timestamp();
    await this.saveSession(session);
    await this.touchProject(session.projectId, session.updatedAt);
    return session;
  }

  async renameSession(sessionId: string, title: string): Promise<AshaSession> {
    const session = await this.requireSession(sessionId);
    session.title = requiredText(title, "Session title");
    session.updatedAt = this.timestamp();
    await this.saveSession(session);
    await this.touchProject(session.projectId, session.updatedAt);
    return session;
  }

  async addLink(sessionId: string, link: HarnessLink): Promise<AshaSession> {
    const session = await this.requireSession(sessionId);
    const normalized = normalizeLink(link);
    if (!session.links.some((existing) => sameLink(existing, normalized))) session.links.push(normalized);
    session.updatedAt = this.timestamp();
    await this.saveSession(session);
    return session;
  }

  async record(sessionId: string, input: SemanticEventInput): Promise<SemanticEvent> {
    const session = await this.requireSession(sessionId);
    if (session.closedAt) throw new Error(`ASHA session '${sessionId}' is closed.`);
    const event = normalizeEvent(input, this.timestamp());
    await mkdir(this.eventsDirectory(), { recursive: true });
    await appendFile(this.eventsPath(session.id), `${JSON.stringify(event)}\n`, "utf8");
    session.updatedAt = event.at;
    await this.saveSession(session);
    await this.touchProject(session.projectId, event.at);
    return event;
  }

  /**
   * Imports a completed local recording without spawning one filesystem update
   * per click. The raw recording remains the source of truth; these are its
   * searchable semantic timeline references.
   */
  async recordMany(sessionId: string, inputs: SemanticEventInput[]): Promise<SemanticEvent[]> {
    const session = await this.requireSession(sessionId);
    if (session.closedAt) throw new Error(`ASHA session '${sessionId}' is closed.`);
    if (!Array.isArray(inputs) || inputs.length === 0) return [];
    const events = inputs.map((input) => normalizeEvent(input, this.timestamp()));
    await mkdir(this.eventsDirectory(), { recursive: true });
    await appendFile(this.eventsPath(session.id), events.map((event) => `${JSON.stringify(event)}\n`).join(""), "utf8");
    session.updatedAt = events.at(-1)!.at;
    await this.saveSession(session);
    await this.touchProject(session.projectId, session.updatedAt);
    return events;
  }

  async closeSession(sessionId: string): Promise<AshaSession> {
    const session = await this.requireSession(sessionId);
    if (!session.closedAt) {
      const now = this.timestamp();
      session.closedAt = now;
      session.updatedAt = now;
      await this.saveSession(session);
      await this.touchProject(session.projectId, now);
    }
    return session;
  }

  async readSession(sessionId: string): Promise<SessionView> {
    const session = await this.requireSession(sessionId);
    return { session, events: await this.readEvents(session.id) };
  }

  async search(query: string, projectId?: string): Promise<SessionSearchResult[]> {
    const needle = requiredText(query, "Search query").toLocaleLowerCase();
    const projects = await this.readProjects();
    const byProject = new Map(projects.map((project) => [project.id, project]));
    const sessions = (await this.readSessions()).filter((session) => !projectId || session.projectId === projectId);
    const results: SessionSearchResult[] = [];
    for (const session of sessions) {
      const project = byProject.get(session.projectId);
      if (!project) continue;
      const eventMatches = (await this.readEvents(session.id)).filter((event) => searchText(event).includes(needle));
      const sessionMatches = searchText(session).includes(needle) || searchText(project).includes(needle);
      if (sessionMatches || eventMatches.length) results.push({ project, session, matchingEvents: eventMatches });
    }
    return results.sort((left, right) => right.session.updatedAt.localeCompare(left.session.updatedAt));
  }

  private async requireSession(id: string): Promise<AshaSession> {
    validateId(id);
    const session = (await this.readSessions()).find((item) => item.id === id);
    if (!session) throw new Error(`No ASHA session named '${id}'.`);
    return structuredClone(session);
  }

  private async saveSession(updated: AshaSession): Promise<void> {
    const sessions = await this.readSessions();
    const index = sessions.findIndex((session) => session.id === updated.id);
    if (index === -1) throw new Error(`No ASHA session named '${updated.id}'.`);
    sessions[index] = updated;
    await this.writeSessions(sessions);
  }

  private async replaceProject(updated: AshaProject): Promise<AshaProject[]> {
    const projects = await this.readProjects();
    const index = projects.findIndex((project) => project.id === updated.id);
    if (index === -1) throw new Error(`No ASHA project named '${updated.id}'.`);
    projects[index] = updated;
    return projects;
  }

  private async touchProject(projectId: string, updatedAt: string): Promise<void> {
    const projects = await this.readProjects();
    const project = projects.find((item) => item.id === projectId);
    if (!project) return;
    project.updatedAt = updatedAt;
    await this.writeProjects(projects);
  }

  private async readProjects(): Promise<AshaProject[]> { return this.readArray<AshaProject>(this.projectsPath()); }
  private async readSessions(): Promise<AshaSession[]> { return this.readArray<AshaSession>(this.sessionsPath()); }

  private async readEvents(sessionId: string): Promise<SemanticEvent[]> {
    try {
      const contents = await readFile(this.eventsPath(sessionId), "utf8");
      return contents.split(/\r?\n/).filter(Boolean).map((line, index) => {
        try { return JSON.parse(line) as SemanticEvent; }
        catch { throw new Error(`ASHA session '${sessionId}' has invalid event JSON on line ${index + 1}.`); }
      });
    } catch (error: unknown) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return [];
      throw error;
    }
  }

  private async readArray<T>(path: string): Promise<T[]> {
    try {
      const parsed = JSON.parse(await readFile(path, "utf8"));
      if (!Array.isArray(parsed)) throw new Error(`ASHA ledger file '${path}' must contain an array.`);
      return parsed as T[];
    } catch (error: unknown) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return [];
      throw error;
    }
  }

  private writeProjects(projects: AshaProject[]): Promise<void> { return writeJsonAtomically(this.projectsPath(), projects); }
  private writeSessions(sessions: AshaSession[]): Promise<void> { return writeJsonAtomically(this.sessionsPath(), sessions); }
  private projectsPath(): string { return join(this.rootDir, "projects.json"); }
  private sessionsPath(): string { return join(this.rootDir, "sessions.json"); }
  private eventsDirectory(): string { return join(this.rootDir, "sessions"); }
  private eventsPath(sessionId: string): string { return join(this.eventsDirectory(), `${sessionId}.jsonl`); }
  private timestamp(): string { return this.now().toISOString(); }
}

function normalizeEvent(input: SemanticEventInput, at: string): SemanticEvent {
  if (!input || typeof input !== "object") throw new Error("A semantic event object is required.");
  if (!['human', 'model', 'system'].includes(input.actor)) throw new Error("Event actor must be human, model, or system.");
  const event: SemanticEvent = { id: makeId("event"), at, actor: input.actor, type: requiredText(input.type, "Event type") };
  if (input.intent) event.intent = shortText(input.intent, "Intent");
  if (input.note) event.note = shortText(input.note, "Note");
  if (input.content) event.content = contentText(input.content, "Event content");
  if (input.target) event.target = normalizeTarget(input.target);
  if (input.cue) event.cue = normalizeCue(input.cue);
  if (input.evidence) event.evidence = normalizeEvidence(input.evidence);
  if (input.reference) event.reference = normalizeLink(input.reference);
  return event;
}

function normalizeEvidence(evidence: VisualEvidence): VisualEvidence {
  if (evidence.privacy !== "local") throw new Error("Visual evidence must be marked local.");
  const normalized: VisualEvidence = { privacy: "local" };
  for (const key of ["beforeFile", "afterFile", "contextFile"] as const) {
    if (evidence[key]) normalized[key] = shortText(evidence[key]!, `Evidence ${key}`);
  }
  if (evidence.changedScore !== undefined) {
    if (!Number.isFinite(evidence.changedScore) || evidence.changedScore < 0) throw new Error("Evidence changedScore must be a non-negative number.");
    normalized.changedScore = evidence.changedScore;
  }
  for (const key of ["contextX", "contextY", "contextWidth", "contextHeight"] as const) {
    if (evidence[key] !== undefined) {
      if (!Number.isFinite(evidence[key])) throw new Error(`Evidence ${key} must be a finite number.`);
      normalized[key] = evidence[key];
    }
  }
  return normalized;
}

function normalizeTarget(target: SemanticTarget): SemanticTarget {
  const result: SemanticTarget = {};
  for (const key of ["app", "label", "control"] as const) {
    if (target[key]) result[key] = shortText(target[key]!, `Target ${key}`);
  }
  for (const key of ["x", "y", "w", "h"] as const) {
    if (target[key] !== undefined && target[key] !== null) {
      if (!Number.isFinite(target[key])) throw new Error(`Target ${key} must be a finite number.`);
      result[key] = target[key];
    }
  }
  return result;
}

function normalizeCue(cue: CueState): CueState {
  const normalized: CueState = {
    id: requiredText(cue?.id, "Cue id"),
    kind: requiredText(cue?.kind, "Cue kind"),
    x: finiteNumber(cue?.x, "Cue x"),
    y: finiteNumber(cue?.y, "Cue y"),
  };
  for (const key of ["w", "h"] as const) {
    if (cue[key] !== undefined && cue[key] !== null) normalized[key] = finiteNumber(cue[key], `Cue ${key}`);
  }
  if (cue.label) normalized.label = shortText(cue.label, "Cue label");
  if (cue.color) normalized.color = shortText(cue.color, "Cue color");
  return normalized;
}

function normalizeLink(link: HarnessLink): HarnessLink {
  const normalized: HarnessLink = { harness: requiredText(link?.harness, "Harness name") };
  if (link.projectId) normalized.projectId = shortText(link.projectId, "Harness project id");
  if (link.sessionId) normalized.sessionId = shortText(link.sessionId, "Harness session id");
  if (link.url) normalized.url = shortText(link.url, "Harness URL");
  return normalized;
}

function sameLink(left: HarnessLink, right: HarnessLink): boolean {
  return left.harness === right.harness && left.projectId === right.projectId && left.sessionId === right.sessionId && left.url === right.url;
}

function searchText(value: unknown): string { return JSON.stringify(value).toLocaleLowerCase(); }
function makeId(prefix: string): string { return `${prefix}-${randomUUID().replaceAll("-", "").slice(0, 16)}`; }
function requiredText(value: unknown, label: string): string {
  if (typeof value !== "string" || !value.trim()) throw new Error(`${label} is required.`);
  return shortText(value, label);
}
function finiteNumber(value: unknown, label: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) throw new Error(`${label} must be a finite number.`);
  return value;
}
function shortText(value: string, label: string): string {
  const normalized = value.trim();
  if (normalized.length > 800) throw new Error(`${label} must be 800 characters or fewer.`);
  return normalized;
}
function contentText(value: string, label: string): string {
  const normalized = value.trim();
  if (normalized.length > 6000) throw new Error(`${label} must be 6000 characters or fewer.`);
  return normalized;
}
function validateId(id: string): void {
  if (!/^[A-Za-z0-9._-]+$/.test(id)) throw new Error("ASHA ids may contain only letters, digits, dots, underscores, and dashes.");
}
async function writeJsonAtomically(path: string, value: unknown): Promise<void> {
  await mkdir(dirname(path), { recursive: true });
  const temporary = `${path}.tmp`;
  await writeFile(temporary, `${JSON.stringify(value, null, 2)}\n`, "utf8");
  await rename(temporary, path);
}
function defaultRuntimeDir(): string { return process.env.ASHA_RUNTIME_DIR ?? join(process.env.LOCALAPPDATA ?? process.cwd(), "asha"); }
