import { action } from "@elgato/streamdeck";
import { ConnStatus, StatusDto } from "../connection/types";
import { Colors, connLabel, humanize, keyTile, wrap } from "../rendering/tiles";
import { KeyTile, StatusAction } from "./base-status-action";

/**
 * Flight Phase (display only) — shows ProsimCompanion's current flight phase
 * from `/api/status` `phase`. Pressing the key does nothing.
 */
@action({ UUID: "com.prosimcompanion.streamdeck.flightphase" })
export class FlightPhaseAction extends StatusAction {
	protected render(tile: KeyTile, _settings: object, state: StatusDto, status: ConnStatus): void | Promise<void> {
		void tile.setTitle("");

		if (status !== "connected") {
			return void tile.setImage(
				keyTile({ background: Colors.idle, heading: "PHASE", value: "—", sub: connLabel(status), dim: true }),
			);
		}

		const phase = (state.phase ?? "").trim();
		if (!phase) {
			return void tile.setImage(keyTile({ background: Colors.idle, heading: "PHASE", value: "—", dim: true }));
		}
		return void tile.setImage(
			keyTile({ background: Colors.callable, heading: "PHASE", lines: wrap(humanize(phase), 10, 2) }),
		);
	}
}
