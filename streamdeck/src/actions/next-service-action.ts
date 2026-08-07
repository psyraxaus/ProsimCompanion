import { action, KeyDownEvent } from "@elgato/streamdeck";
import { connection } from "../connection/connection-manager";
import { ConnStatus, StatusDto } from "../connection/types";
import { Colors, connLabel, humanize, keyTile, wrap } from "../rendering/tiles";
import { KeyTile, showCommandResult, StatusAction } from "./base-status-action";

/**
 * Next Service — shows the ground-automation's `gsx.nextService` hint; press
 * POSTs `gsx.forceNextService` to trigger it immediately instead of waiting for
 * the automation's own timing.
 */
@action({ UUID: "com.prosimcompanion.streamdeck.nextservice" })
export class NextServiceAction extends StatusAction {
	protected render(tile: KeyTile, _settings: object, state: StatusDto, status: ConnStatus): void | Promise<void> {
		void tile.setTitle("");

		if (status !== "connected") {
			return void tile.setImage(
				keyTile({ background: Colors.idle, heading: "NEXT", value: "—", sub: connLabel(status), dim: true }),
			);
		}

		const gsx = state.gsx ?? {};
		const next = (gsx.nextService ?? "").trim();
		if (!next) {
			return void tile.setImage(keyTile({ background: Colors.idle, heading: "NEXT", value: "—", dim: true }));
		}
		return void tile.setImage(
			keyTile({
				background: Colors.callable,
				heading: "NEXT",
				lines: wrap(humanize(next), 11, 3),
				sub: gsx.automationActive ? "press to force" : undefined,
			}),
		);
	}

	override async onKeyDown(ev: KeyDownEvent): Promise<void> {
		const tile = ev.action as unknown as KeyTile;
		const res = await connection.post("gsx.forceNextService");
		await showCommandResult(tile, res);
	}
}
