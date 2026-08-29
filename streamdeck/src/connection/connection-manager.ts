import streamDeck from "@elgato/streamdeck";
import { EventEmitter } from "node:events";
import { ConnectionSettings, ConnStatus, DEFAULT_SETTINGS, PostResult, StatusDto } from "./types";

/** Normal poll cadence while the connection is healthy. */
const POLL_INTERVAL_MS = 1_000;
/** Failure backoff cap. */
const MAX_BACKOFF_MS = 30_000;

/**
 * The single connection to a running ProsimCompanion instance. REST-only:
 * polls `GET /api/status` once a second while at least one key is visible
 * (actions call {@link retain}/{@link release} from willAppear/willDisappear),
 * and fires commands with `POST /api/command/{name}`. Every key reads from this
 * shared instance — no per-key pollers.
 *
 * Failure handling: transport errors back off exponentially (capped 30 s) and
 * surface as "connecting"; HTTP 401 surfaces as "reauth" (token rotated —
 * re-pair needed); HTTP 404 on /api/status surfaces as "apiDisabled"
 * (`commandApi.enabled` is off in ProsimCompanion). All three keep polling so
 * the keys recover on their own once the server side is fixed.
 *
 * Events:
 *   "state"  (state: StatusDto)   — fired on every successful status poll
 *   "status" (status: ConnStatus) — fired on connection lifecycle changes
 *   "reauth" (reason: string)     — token rejected/rotated; re-pairing needed
 */
export class ConnectionManager extends EventEmitter {
	private settings: ConnectionSettings = { ...DEFAULT_SETTINGS };
	private state: StatusDto = {};
	private status: ConnStatus = "unconfigured";
	private failures = 0;
	private refCount = 0;
	private timer?: ReturnType<typeof setTimeout>;
	/** Guards against acting on responses from a poll loop we've already superseded. */
	private generation = 0;

	get currentState(): StatusDto {
		return this.state;
	}

	get connectionStatus(): ConnStatus {
		return this.status;
	}

	get baseUrl(): string {
		return `http://${this.settings.host}:${this.settings.port}`;
	}

	get isConfigured(): boolean {
		return !!this.settings.token && !!this.settings.host && this.settings.port > 0;
	}

	/** Apply (possibly changed) connection settings and restart the poll loop if it is running. */
	configure(settings: Partial<ConnectionSettings>): void {
		const next: ConnectionSettings = {
			host: settings.host?.trim() || DEFAULT_SETTINGS.host,
			port: Number(settings.port) || DEFAULT_SETTINGS.port,
			token: settings.token?.trim() ?? "",
		};
		const changed =
			next.host !== this.settings.host || next.port !== this.settings.port || next.token !== this.settings.token;
		this.settings = next;
		if (!this.isConfigured) {
			this.setStatus("unconfigured");
		}
		if (changed) {
			this.failures = 0;
			this.restart();
		}
	}

	/**
	 * A key became visible. The poller runs only while the ref-count is above
	 * zero — Stream Deck folders/profiles hide keys constantly and there is no
	 * point polling a sim PC for tiles nobody can see.
	 */
	retain(): void {
		this.refCount++;
		if (this.refCount === 1) {
			this.failures = 0;
			this.restart();
		}
	}

	/** A key disappeared. Stops the poller when the last one goes. */
	release(): void {
		this.refCount = Math.max(0, this.refCount - 1);
		if (this.refCount === 0) {
			this.stopPolling();
		}
	}

	/** Tear everything down (plugin shutdown). */
	stop(): void {
		this.refCount = 0;
		this.stopPolling();
		this.setStatus("disconnected");
	}

	/**
	 * Poll now instead of waiting for the timer — used right after a command that
	 * flips visible state (the Voice Pause toggle) so the key face follows within
	 * one round-trip rather than up to a full poll period. No-op with no visible keys.
	 */
	refresh(): void {
		this.restart();
	}

	// ── Poll loop ───────────────────────────────────────────────────────────

	private restart(): void {
		this.clearTimer();
		if (this.refCount === 0) return;
		const gen = ++this.generation;
		void this.pollOnce(gen);
	}

	private stopPolling(): void {
		this.generation++; // invalidate any in-flight poll
		this.clearTimer();
	}

	private schedule(gen: number, delay: number): void {
		if (gen !== this.generation) return;
		this.clearTimer();
		this.timer = setTimeout(() => void this.pollOnce(gen), delay);
	}

	private async pollOnce(gen: number): Promise<void> {
		if (gen !== this.generation) return;
		if (!this.isConfigured) {
			this.setStatus("unconfigured");
			// Re-check occasionally in case settings arrive without a configure() call.
			this.schedule(gen, 2_000);
			return;
		}

		let res: Response | undefined;
		try {
			res = await fetch(`${this.baseUrl}/api/status`, {
				headers: { Authorization: `Bearer ${this.settings.token}` },
				signal: AbortSignal.timeout(5_000),
			});
		} catch (err) {
			if (gen !== this.generation) return;
			streamDeck.logger.debug(`status poll failed: ${String(err)}`);
			this.failed(gen, "connecting");
			return;
		}
		if (gen !== this.generation) return;

		if (res.status === 401) {
			// Token rotated / rejected — a fresh pairing is needed, but keep
			// polling (backed off) so pasting a new token recovers by itself.
			this.setStatus("reauth");
			this.emit("reauth", "REST 401 (token rotated)");
			this.failed(gen, "reauth");
			return;
		}
		if (res.status === 404) {
			// The web server answered but the command API surface is not there:
			// commandApi.enabled is off in ProsimCompanion's settings.
			this.failed(gen, "apiDisabled");
			return;
		}
		if (!res.ok) {
			this.failed(gen, "connecting");
			return;
		}

		let dto: StatusDto;
		try {
			dto = (await res.json()) as StatusDto;
		} catch {
			this.failed(gen, "connecting");
			return;
		}
		if (gen !== this.generation) return;

		this.failures = 0;
		this.state = dto ?? {};
		this.setStatus("connected");
		this.emit("state", this.state);
		this.schedule(gen, POLL_INTERVAL_MS);
	}

	/** Register a failed poll: set the status, back off exponentially (capped), re-schedule. */
	private failed(gen: number, status: ConnStatus): void {
		this.setStatus(status);
		const delay = Math.min(MAX_BACKOFF_MS, POLL_INTERVAL_MS * 2 ** this.failures);
		this.failures++;
		this.schedule(gen, delay);
	}

	private clearTimer(): void {
		if (this.timer) {
			clearTimeout(this.timer);
			this.timer = undefined;
		}
	}

	// ── Commands ────────────────────────────────────────────────────────────

	/**
	 * POST /api/command/{name}. Never throws. Surfaces 401 as a reauth signal.
	 * `ok` is true when the server accepted the command (HTTP 2xx) — including
	 * `alreadySatisfied`, which is a benign no-op, not an error.
	 */
	async post(name: string, body?: unknown): Promise<PostResult> {
		if (!this.isConfigured) {
			return { ok: false, status: 0, reason: "Not paired — set host/port/token." };
		}
		try {
			const res = await fetch(`${this.baseUrl}/api/command/${name}`, {
				method: "POST",
				headers: {
					Authorization: `Bearer ${this.settings.token}`,
					"Content-Type": "application/json",
				},
				body: body !== undefined ? JSON.stringify(body) : undefined,
				signal: AbortSignal.timeout(10_000),
			});

			let data: { outcome?: string; reason?: string } | null = null;
			try {
				data = (await res.json()) as { outcome?: string; reason?: string };
			} catch {
				/* empty / non-JSON body */
			}

			if (res.status === 401) this.emit("reauth", "REST 401 (token rotated)");
			return { ok: res.ok, status: res.status, outcome: data?.outcome, reason: data?.reason };
		} catch (err) {
			return { ok: false, status: 0, reason: `Request failed: ${String(err)}` };
		}
	}

	private setStatus(status: ConnStatus): void {
		if (status === this.status) return;
		this.status = status;
		this.emit("status", status);
	}
}

/** The single shared connection used by every action. */
export const connection = new ConnectionManager();
