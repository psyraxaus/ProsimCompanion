import streamDeck, { action, KeyDownEvent, PropertyInspectorDidAppearEvent, SendToPluginEvent } from "@elgato/streamdeck";
import { connection } from "../connection/connection-manager";
import { ConnStatus, StatusDto } from "../connection/types";
import { cameraTile, Flash } from "../rendering/cockpit-tiles";
import { connLabel } from "../rendering/tiles";
import { KeyTile, shorten, StatusAction } from "./base-status-action";

type CameraViewSettings = {
	/** A view row name from ProsimCompanion → Settings → Setup → vJoy relay. */
	view?: string;
};

/**
 * Camera view (2026-10-24): presses the vJoy button a view row maps on the sim PC, so a
 * ChasePlane view bound to that button changes from a key on any PC. POSTs
 * `input.pressView {name}`; the face names the view and dims while the relay is off.
 */
@action({ UUID: "com.prosimcompanion.streamdeck.cameraview" })
export class CameraViewAction extends StatusAction<CameraViewSettings> {
	private readonly flashes = new Map<string, { flash: Flash; note?: string; until: number }>();

	protected render(tile: KeyTile, settings: CameraViewSettings, _state: StatusDto, status: ConnStatus): void | Promise<void> {
		void tile.setTitle("");
		const view = (settings.view ?? "").trim();
		const f = this.flashes.get(tile.id);
		const flash = f && f.until > Date.now() ? f : undefined;

		if (status !== "connected") {
			return void tile.setImage(cameraTile(view, connLabel(status), true));
		}

		const controls = connection.controls;
		if (view.length === 0) {
			return void tile.setImage(cameraTile("", "set up", true));
		}
		if (controls?.viewsEnabled === false) {
			return void tile.setImage(cameraTile(view, "relay off", true, flash?.flash));
		}
		const known = (controls?.views ?? []).some((v) => v.name.toLowerCase() === view.toLowerCase());
		return void tile.setImage(cameraTile(view, flash?.note ?? (known ? undefined : "not found"), !known, flash?.flash));
	}

	override async onKeyDown(ev: KeyDownEvent<CameraViewSettings>): Promise<void> {
		const tile = ev.action as unknown as KeyTile;
		const view = (ev.payload.settings.view ?? "").trim();
		if (view.length === 0) {
			await tile.showAlert();
			return;
		}

		const res = await connection.post("input.pressView", { name: view });
		const good = res.ok && (res.outcome === undefined || res.outcome === "success" || res.outcome === "alreadySatisfied");
		this.flashes.set(tile.id, good
			? { flash: "ok", until: Date.now() + 250 }
			: { flash: "bad", note: shorten(res.reason ?? "refused", 18), until: Date.now() + 2_000 });
		void this.render(tile, ev.payload.settings, connection.currentState, connection.connectionStatus);
		setTimeout(() => {
			this.flashes.delete(tile.id);
			void this.render(tile, this.settingsOf(tile.id) ?? ev.payload.settings, connection.currentState, connection.connectionStatus);
		}, good ? 260 : 2_010);
		if (!good) {
			await tile.showAlert();
		}
	}

	override onPropertyInspectorDidAppear(_ev: PropertyInspectorDidAppearEvent<CameraViewSettings>): void {
		void this.sendViews();
	}

	override onSendToPlugin(_ev: SendToPluginEvent<{ event?: string }, CameraViewSettings>): void {
		void this.sendViews();
	}

	private async sendViews(): Promise<void> {
		const controls = (await connection.fetchControls()) ?? connection.controls;
		await streamDeck.ui.current?.sendToPropertyInspector({
			event: "views",
			views: (controls?.views ?? []).map((v) => ({ name: v.name, button: v.button })),
			enabled: controls?.viewsEnabled !== false,
			connected: connection.connectionStatus === "connected",
		});
	}
}
