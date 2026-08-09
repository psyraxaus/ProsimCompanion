import { action, KeyDownEvent } from "@elgato/streamdeck";
import { connection } from "../connection/connection-manager";
import { ConnStatus, GsxStatusDto, ServiceState, StatusDto } from "../connection/types";
import { Colors, connLabel, keyTile, stateColor, stateShort } from "../rendering/tiles";
import { KeyTile, showCommandResult, StatusAction } from "./base-status-action";

type GsxServiceSettings = {
	service?: string;
};

interface ServiceDef {
	heading: string;
	/** POST /api/command/{command} */
	command: string;
	/** `gsx.services[].type` value this key mirrors (case-insensitive match). */
	statusType?: string;
	/** Optional live value shown while requested/active (pax count, refuel %). */
	getValue?: (g: GsxStatusDto, state: ServiceState | undefined) => string | undefined;
}

function inProgress(s: ServiceState | undefined): boolean {
	return s === "requested" || s === "active";
}

// statusType values are the CANONICAL GSX Remote API service ids (GsxServiceIds in the app,
// docs/integrations/gsx-remote-api.md §3) — `gsx.services[].type` carries those verbatim.
// Short names ("refuel", "jetway") match nothing and leave the key permanently dim (#31).
const SERVICES: Record<string, ServiceDef> = {
	refuel: {
		heading: "REFUEL",
		command: "gsx.requestRefuel",
		statusType: "Refueling",
		getValue: (g, s) => (inProgress(s) ? `${Math.round(g.refuelPercent ?? 0)}%` : undefined),
	},
	catering: { heading: "CATERING", command: "gsx.requestCatering", statusType: "Catering" },
	boarding: {
		heading: "BOARDING",
		command: "gsx.requestBoarding",
		statusType: "Boarding",
		getValue: (g, s) => (inProgress(s) ? `${g.paxBoarded ?? 0}/${g.paxTotal ?? 0}` : undefined),
	},
	deboarding: {
		heading: "DEBOARD",
		command: "gsx.requestDeboarding",
		statusType: "Deboarding",
		// paxRemaining counts down as pax leave; the boarding counter freezes during a
		// deboard, which left this key stuck at total/total (issue #37).
		getValue: (g, s) => (inProgress(s) ? `${g.paxRemaining ?? g.paxBoarded ?? 0}/${g.paxTotal ?? 0}` : undefined),
	},
	jetway: { heading: "JETWAY", command: "gsx.requestJetway", statusType: "OperateJetways" },
	jetwayRetract: { heading: "JETWAY ▲", command: "gsx.retractJetway", statusType: "OperateJetways" },
	stairs: { heading: "STAIRS", command: "gsx.requestStairs", statusType: "OperateStairs" },
	stairsRetract: { heading: "STAIRS ▲", command: "gsx.retractStairs", statusType: "OperateStairs" },
	gpu: { heading: "GPU", command: "gsx.requestGpu", statusType: "GPU" },
	deice: { heading: "DE-ICE", command: "gsx.requestDeice", statusType: "DeIce" },
	// GSX names the pushback request service "Departure".
	pushback: { heading: "PUSHBACK", command: "gsx.requestPushback", statusType: "Departure" },
	departure: { heading: "AUTO DEP", command: "gsx.startDepartureServices" },
};

/**
 * One configurable action for any GSX ground service. The Property Inspector
 * picks which service (including the retract variants); the key mirrors that
 * service's live state from `gsx.services[]` (colour-coded, with pax `n/total`
 * or refuel `%` where relevant) and, on press, POSTs the matching
 * request/retract command. Greyed out when the service is `notAvailable` —
 * the server still rejects out-of-phase requests, this is just the visual gate.
 */
@action({ UUID: "com.prosimcompanion.streamdeck.gsxservice" })
export class GsxServiceAction extends StatusAction<GsxServiceSettings> {
	protected render(
		tile: KeyTile,
		settings: GsxServiceSettings,
		state: StatusDto,
		status: ConnStatus,
	): void | Promise<void> {
		const def = SERVICES[settings.service ?? "refuel"] ?? SERVICES.refuel;
		void tile.setTitle("");

		if (status !== "connected") {
			return void tile.setImage(
				keyTile({ background: Colors.idle, heading: def.heading, value: "—", sub: connLabel(status), dim: true }),
			);
		}

		const gsx = state.gsx ?? {};

		if (!def.statusType) {
			// startDepartureServices has no per-service entry; reflect the automation flag.
			const running = gsx.automationActive === true;
			return void tile.setImage(
				keyTile({
					background: running ? Colors.active : Colors.callable,
					heading: def.heading,
					value: running ? "RUNNING" : "START",
				}),
			);
		}

		const entry = (gsx.services ?? []).find(
			(s) => (s.type ?? "").toLowerCase() === def.statusType!.toLowerCase(),
		);
		const svcState = entry?.state;
		const value = def.getValue?.(gsx, svcState) ?? stateShort(svcState);
		const dim = svcState === undefined || svcState === "notAvailable" || svcState === "skipped";

		return void tile.setImage(
			keyTile({
				background: stateColor(svcState),
				heading: def.heading,
				value,
				sub: entry?.detail ?? svcState ?? "—",
				dim,
			}),
		);
	}

	override async onKeyDown(ev: KeyDownEvent<GsxServiceSettings>): Promise<void> {
		const tile = ev.action as unknown as KeyTile;
		const def = SERVICES[ev.payload.settings.service ?? "refuel"] ?? SERVICES.refuel;
		const res = await connection.post(def.command);
		await showCommandResult(tile, res);
	}
}
