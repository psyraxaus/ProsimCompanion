import { action } from "@elgato/streamdeck";
import { ConnStatus, StatusDto } from "../connection/types";
import { Colors, healthTile, keyTile } from "../rendering/tiles";
import { KeyTile, StatusAction } from "./base-status-action";

/**
 * Connection Health (display only) — three per-connection dots (ProSim / MSFS /
 * GSX) from `/api/status` `connections`, with distinct tile states for the
 * plugin-side failure modes: unconfigured (never paired), connecting (can't
 * reach ProsimCompanion), re-pair (token rotated, HTTP 401), and API disabled
 * (`commandApi.enabled` off, HTTP 404).
 */
@action({ UUID: "com.prosimcompanion.streamdeck.health" })
export class ConnectionHealthAction extends StatusAction {
	protected render(tile: KeyTile, _settings: object, state: StatusDto, status: ConnStatus): void | Promise<void> {
		void tile.setTitle("");

		switch (status) {
			case "unconfigured":
				return void tile.setImage(
					keyTile({ background: Colors.idle, heading: "LINK", value: "SET UP", sub: "open key settings", dim: true }),
				);
			case "connecting":
			case "disconnected":
				return void tile.setImage(
					keyTile({ background: Colors.disabled, heading: "LINK", value: "…", sub: "connecting", dot: Colors.warn }),
				);
			case "reauth":
				return void tile.setImage(
					keyTile({ background: Colors.error, heading: "LINK", value: "RE-PAIR", sub: "token rejected" }),
				);
			case "apiDisabled":
				return void tile.setImage(
					keyTile({ background: Colors.warn, heading: "LINK", value: "API OFF", sub: "enable command API" }),
				);
		}

		const conns = state.connections ?? {};
		const prosim = conns.prosim === true;
		const msfs = conns.msfs === true;
		const gsx = conns.gsx === true;
		const up = [prosim, msfs, gsx].filter(Boolean).length;
		const bg = up === 3 ? Colors.completed : up === 0 ? Colors.error : Colors.idle;

		return void tile.setImage(
			healthTile(bg, [
				{ label: "PSM", up: prosim },
				{ label: "MSFS", up: msfs },
				{ label: "GSX", up: gsx },
			]),
		);
	}
}
