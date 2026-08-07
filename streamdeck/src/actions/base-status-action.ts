import streamDeck, {
	DidReceiveSettingsEvent,
	JsonObject,
	SingletonAction,
	WillAppearEvent,
	WillDisappearEvent,
} from "@elgato/streamdeck";
import { connection } from "../connection/connection-manager";
import { ConnStatus, StatusDto } from "../connection/types";

/**
 * Minimal structural view of a Keypad action — the surface this plugin uses.
 * Declared structurally so we don't depend on exact exported type names across
 * @elgato/streamdeck minor versions.
 */
export interface KeyTile {
	readonly id: string;
	setTitle(title?: string): Promise<void>;
	setImage(image?: string): Promise<void>;
	showOk(): Promise<void>;
	showAlert(): Promise<void>;
}

/**
 * Base for every action that reflects live state. Tracks the currently-visible
 * key instances (retaining the shared poller per visible key so it only runs
 * while at least one key can be seen), subscribes ONCE to the shared
 * connection's state/status/reauth events, and re-renders all visible keys on
 * any change. Subclasses implement `render` (and usually `onKeyDown`).
 */
export abstract class StatusAction<TSettings extends JsonObject = JsonObject> extends SingletonAction<TSettings> {
	private readonly visible = new Map<string, { tile: KeyTile; settings: TSettings }>();
	private wired = false;

	/** Draw a single key from current state. */
	protected abstract render(
		tile: KeyTile,
		settings: TSettings,
		state: StatusDto,
		status: ConnStatus,
	): void | Promise<void>;

	private ensureWired(): void {
		if (this.wired) return;
		this.wired = true;
		const rerender = (): void => this.renderAll();
		connection.on("state", rerender);
		connection.on("status", rerender);
		connection.on("reauth", rerender);
	}

	override onWillAppear(ev: WillAppearEvent<TSettings>): void | Promise<void> {
		this.ensureWired();
		const tile = ev.action as unknown as KeyTile;
		if (!this.visible.has(tile.id)) {
			connection.retain();
		}
		this.visible.set(tile.id, { tile, settings: ev.payload.settings });
		return this.safeRender(tile, ev.payload.settings);
	}

	override onWillDisappear(ev: WillDisappearEvent<TSettings>): void {
		if (this.visible.delete(ev.action.id)) {
			connection.release();
		}
	}

	override onDidReceiveSettings(ev: DidReceiveSettingsEvent<TSettings>): void | Promise<void> {
		const tile = ev.action as unknown as KeyTile;
		const known = this.visible.has(tile.id);
		if (!known) {
			// Settings can arrive before willAppear; count the key as visible once.
			connection.retain();
		}
		this.visible.set(tile.id, { tile, settings: ev.payload.settings });
		return this.safeRender(tile, ev.payload.settings);
	}

	protected renderAll(): void {
		for (const { tile, settings } of this.visible.values()) {
			void this.safeRender(tile, settings);
		}
	}

	private async safeRender(tile: KeyTile, settings: TSettings): Promise<void> {
		try {
			await this.render(tile, settings, connection.currentState, connection.connectionStatus);
		} catch (err) {
			streamDeck.logger.error(`render failed: ${String(err)}`);
		}
	}
}

/** Flash key feedback for a command result and surface the server's reason, if any. */
export async function showCommandResult(
	tile: KeyTile,
	res: { ok: boolean; outcome?: string; reason?: string },
): Promise<void> {
	const good = res.ok && (res.outcome === undefined || res.outcome === "success" || res.outcome === "alreadySatisfied");
	await (good ? tile.showOk() : tile.showAlert());
	if (!good && res.reason) {
		// Briefly surface the server's reason on the key title; the next state
		// render clears it.
		void tile.setTitle(shorten(res.reason));
	}
}

function shorten(s: string, max = 22): string {
	return s.length > max ? s.slice(0, max - 1) + "…" : s;
}
