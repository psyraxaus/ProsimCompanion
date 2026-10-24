import streamDeck, {
	action,
	KeyDownEvent,
	KeyUpEvent,
	PropertyInspectorDidAppearEvent,
	SendToPluginEvent,
} from "@elgato/streamdeck";
import { connection } from "../connection/connection-manager";
import { ConnStatus, ControlsSwitchDto, StatusDto, SwitchStatusDto } from "../connection/types";
import { blankTile, Flash, guardedTile, knobTile, korryTile, LampDefaults, toggleTile } from "../rendering/cockpit-tiles";
import { connLabel } from "../rendering/tiles";
import { KeyTile, shorten, StatusAction } from "./base-status-action";

type CockpitSwitchSettings = {
	/** The row id from ProsimCompanion → Settings → Cockpit Switches. */
	switchId?: string;
};

/** A long press (a cycling selector steps backwards) starts here. */
const LONG_PRESS_MS = 600;
const FLASH_OK_MS = 250;
const FLASH_BAD_MS = 2_000;

interface FlashState {
	flash: Flash;
	note?: string;
	until: number;
}

/**
 * Cockpit switch (2026-10-24): one key per ProSim switch the pilot configured in the app —
 * drawn as a Korry push button, toggle, rotary knob or guarded switch from `/api/controls`
 * (look, legends, colours, positions) and lit from `/api/status` `switches[]` (value, lamps).
 * Press POSTs `cockpit.press {id, long}`; the app writes the dataref over the ProSim SDK.
 * Feedback rides on the face edge: a short blue ring when the app accepted, an amber ring
 * plus the reason when it refused.
 */
@action({ UUID: "com.prosimcompanion.streamdeck.cockpitswitch" })
export class CockpitSwitchAction extends StatusAction<CockpitSwitchSettings> {
	private readonly pressedAt = new Map<string, number>();
	private readonly flashes = new Map<string, FlashState>();

	protected render(tile: KeyTile, settings: CockpitSwitchSettings, state: StatusDto, status: ConnStatus): void | Promise<void> {
		void tile.setTitle("");
		const id = (settings.switchId ?? "").trim();
		const flash = this.currentFlash(tile.id);

		if (status !== "connected") {
			return void tile.setImage(blankTile(id.length > 0 ? id : "SWITCH", connLabel(status)));
		}

		const controls = connection.controls;
		const def = controls?.switches?.find((s) => s.id === id);
		if (!def) {
			return void tile.setImage(blankTile("SWITCH", id.length > 0 ? "not found" : "set up"));
		}

		const live = state.switches?.find((s) => s.id === id);
		const off = controls?.switchesEnabled === false;
		const noValue = !live || live.value === null || live.value === undefined;
		const dim = off || noValue || live?.stale === true;
		const note = flash?.note ?? (off ? "switches off" : live?.stale ? "prosim away" : noValue ? "no value" : undefined);
		return void tile.setImage(faceFor(def, live, dim, flash?.flash, note));
	}

	override onKeyDown(ev: KeyDownEvent<CockpitSwitchSettings>): void {
		this.pressedAt.set(ev.action.id, Date.now());
	}

	override async onKeyUp(ev: KeyUpEvent<CockpitSwitchSettings>): Promise<void> {
		const tile = ev.action as unknown as KeyTile;
		const id = (ev.payload.settings.switchId ?? "").trim();
		const started = this.pressedAt.get(tile.id) ?? Date.now();
		this.pressedAt.delete(tile.id);
		if (id.length === 0) {
			await tile.showAlert();
			return;
		}

		const long = Date.now() - started >= LONG_PRESS_MS;
		const res = await connection.post("cockpit.press", { id, long });
		const good = res.ok && (res.outcome === undefined || res.outcome === "success" || res.outcome === "alreadySatisfied");
		this.flash(tile, good ? { flash: "ok", until: Date.now() + FLASH_OK_MS } : { flash: "bad", note: shorten(res.reason ?? "refused", 18), until: Date.now() + FLASH_BAD_MS });
		if (!good) {
			await tile.showAlert();
		}
		// Follow the switch within one round-trip rather than a full poll period.
		void connection.refresh();
	}

	override onPropertyInspectorDidAppear(_ev: PropertyInspectorDidAppearEvent<CockpitSwitchSettings>): void {
		void this.sendControls();
	}

	override onSendToPlugin(_ev: SendToPluginEvent<{ event?: string }, CockpitSwitchSettings>): void {
		void this.sendControls();
	}

	/** The Property Inspector's dropdown: the configured switches, fresh from the app. */
	private async sendControls(): Promise<void> {
		const controls = (await connection.fetchControls()) ?? connection.controls;
		const switches = (controls?.switches ?? []).map((s) => ({ id: s.id, name: s.name }));
		await streamDeck.ui.current?.sendToPropertyInspector({
			event: "controls",
			switches,
			enabled: controls?.switchesEnabled !== false,
			connected: connection.connectionStatus === "connected",
		});
	}

	private currentFlash(tileId: string): FlashState | undefined {
		const f = this.flashes.get(tileId);
		if (f && f.until > Date.now()) return f;
		if (f) this.flashes.delete(tileId);
		return undefined;
	}

	private flash(tile: KeyTile, state: FlashState): void {
		this.flashes.set(tile.id, state);
		const settings = this.settingsOf(tile.id) ?? {};
		void this.render(tile, settings, connection.currentState, connection.connectionStatus);
		setTimeout(() => {
			this.flashes.delete(tile.id);
			void this.render(tile, this.settingsOf(tile.id) ?? {}, connection.currentState, connection.connectionStatus);
		}, state.until - Date.now() + 10);
	}
}

/** The on/off word pair for toggles and guarded switches: [up/on label, down/off label]. */
function pair(def: ControlsSwitchDto): [string, string] {
	const p = def.positions ?? [];
	return p.length >= 2 ? [p[1].label, p[0].label] : ["ON", "OFF"];
}

function faceFor(def: ControlsSwitchDto, live: SwitchStatusDto | undefined, dim: boolean, flash: Flash, note?: string): string {
	const bezel = def.bezelColor && def.bezelColor.length > 0 ? def.bezelColor : undefined;
	const lower = def.lowerColor ?? LampDefaults.lower;
	const showUnlit = def.showUnlitLegend !== false;
	const positions = def.positions ?? [];
	const value = live?.value ?? null;
	const on = value !== null && value !== 0 && (positions.length < 2 || value === positions[1].value || positions.length > 2);

	switch (def.look ?? "korry2") {
		case "korry1":
			return korryTile({
				title: def.name,
				bezel,
				lower: { legend: def.lowerLegend ?? "", color: lower, lit: live?.lowerLit === true, showUnlit },
				dim,
				flash,
				note,
			});
		case "toggle":
			return toggleTile({ title: def.name, bezel, up: on, labels: pair(def), activeColor: lower, dim, flash, note });
		case "knob": {
			const labels = positions.length > 0 ? positions.map((p) => p.label) : ["0", "1"];
			const index = value === null ? -1 : positions.findIndex((p) => p.value === value);
			return knobTile({ title: def.name, bezel, positions: labels, index, activeColor: lower, dim, flash, note });
		}
		case "guarded":
			return guardedTile({ title: def.name, bezel, on, labels: pair(def), activeColor: lower, dim, flash, note });
		case "korry2":
		default:
			return korryTile({
				title: def.name,
				bezel,
				upper: { legend: def.upperLegend ?? "", color: def.upperColor ?? LampDefaults.upper, lit: live?.upperLit === true, showUnlit },
				lower: { legend: def.lowerLegend ?? "", color: lower, lit: live?.lowerLit === true, showUnlit },
				dim,
				flash,
				note,
			});
	}
}
