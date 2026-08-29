import streamDeck, { LogLevel } from "@elgato/streamdeck";
import { ChecklistAction } from "./actions/checklist-action";
import { ConnectionHealthAction } from "./actions/connection-health-action";
import { FlightPhaseAction } from "./actions/flight-phase-action";
import { GsxServiceAction } from "./actions/gsx-service-action";
import { NextServiceAction } from "./actions/next-service-action";
import { VoicePauseAction } from "./actions/voice-pause-action";
import { connection } from "./connection/connection-manager";
import { ConnectionSettings, DEFAULT_SETTINGS } from "./connection/types";

streamDeck.logger.setLevel(LogLevel.INFO);

// Register every action. They all read from the single shared `connection`.
streamDeck.actions.registerAction(new GsxServiceAction());
streamDeck.actions.registerAction(new NextServiceAction());
streamDeck.actions.registerAction(new ChecklistAction());
streamDeck.actions.registerAction(new FlightPhaseAction());
streamDeck.actions.registerAction(new ConnectionHealthAction());
streamDeck.actions.registerAction(new VoicePauseAction());

// Connection settings live in Stream Deck *global* settings (shared by all keys).
// Reconfigure whenever a Property Inspector saves them.
streamDeck.settings.onDidReceiveGlobalSettings<Partial<ConnectionSettings>>((ev) => {
	connection.configure({ ...DEFAULT_SETTINGS, ...(ev.settings ?? {}) });
});

connection.on("reauth", (reason: string) => {
	streamDeck.logger.warn(
		`Re-pair required — ${reason}. Open any ProsimCompanion key's settings and paste a fresh token / pairing URL.`,
	);
});

// Connect to Stream Deck, then pull the persisted connection settings.
streamDeck.connect().then(async () => {
	const global = (await streamDeck.settings.getGlobalSettings<Partial<ConnectionSettings>>()) ?? {};
	connection.configure({ ...DEFAULT_SETTINGS, ...global });
});
