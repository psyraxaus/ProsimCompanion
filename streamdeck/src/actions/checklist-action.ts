import { action, KeyDownEvent } from "@elgato/streamdeck";
import { connection } from "../connection/connection-manager";
import { ConnStatus, StatusDto } from "../connection/types";
import { Colors, connLabel, keyTile, wrap } from "../rendering/tiles";
import { KeyTile, showCommandResult, StatusAction } from "./base-status-action";

type ChecklistSettings = {
	fn?: string; // "advance" | "restart"
};

const FNS: Record<string, { command: string; sub: string }> = {
	advance: { command: "checklists.advanceNext", sub: "advance" },
	restart: { command: "checklists.restart", sub: "restart" },
};

/**
 * Checklist — shows the active checklist's name and current item (with an
 * `index/count` progress marker). The Property Inspector picks the function:
 * Advance (`checklists.advanceNext`) or Restart (`checklists.restart`).
 */
@action({ UUID: "com.prosimcompanion.streamdeck.checklist" })
export class ChecklistAction extends StatusAction<ChecklistSettings> {
	protected render(
		tile: KeyTile,
		settings: ChecklistSettings,
		state: StatusDto,
		status: ConnStatus,
	): void | Promise<void> {
		const fn = settings.fn === "restart" ? "restart" : "advance";
		const def = FNS[fn];
		void tile.setTitle("");

		if (status !== "connected") {
			return void tile.setImage(
				keyTile({ background: Colors.idle, heading: "CHECKLIST", value: "—", sub: connLabel(status), dim: true }),
			);
		}

		const cl = state.checklist ?? {};
		const name = (cl.name ?? "").trim();
		const item = (cl.item ?? "").trim();
		const progress =
			typeof cl.index === "number" && typeof cl.count === "number" && cl.count > 0
				? ` ${cl.index}/${cl.count}`
				: "";

		if (!name && !item) {
			// No active checklist — still show which function this key fires.
			return void tile.setImage(
				keyTile({
					background: fn === "restart" ? Colors.completed : Colors.idle,
					heading: "CHECKLIST",
					value: fn.toUpperCase(),
					dim: true,
				}),
			);
		}

		return void tile.setImage(
			keyTile({
				background: fn === "restart" ? Colors.completed : Colors.callable,
				heading: shorten(name.toUpperCase(), 14) || "CHECKLIST",
				lines: item ? wrap(item, 11, 3) : [fn.toUpperCase()],
				sub: `${def.sub}${progress}`,
			}),
		);
	}

	override async onKeyDown(ev: KeyDownEvent<ChecklistSettings>): Promise<void> {
		const tile = ev.action as unknown as KeyTile;
		const fn = ev.payload.settings.fn === "restart" ? "restart" : "advance";
		const res = await connection.post(FNS[fn].command);
		await showCommandResult(tile, res);
	}
}

function shorten(s: string, max: number): string {
	return s.length > max ? s.slice(0, max - 1) + "…" : s;
}
