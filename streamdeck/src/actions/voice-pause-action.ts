import { action, KeyDownEvent } from "@elgato/streamdeck";
import { connection } from "../connection/connection-manager";
import { ConnStatus, StatusDto } from "../connection/types";
import { Colors, connLabel, keyTile } from "../rendering/tiles";
import { KeyTile, showCommandResult, StatusAction } from "./base-status-action";

/**
 * Voice Pause — a toggle key that mutes the First Officer's *ear* (speech recognition)
 * while the pilot talks to a real person. The FO still speaks; only listening stops.
 * Press POSTs `speech.toggleListening`; the face follows `/api/status` `voice`:
 *   - red   PAUSED    — the pilot's latch is on (press again to resume)
 *   - green LISTENING — the recognizer is actually capturing
 *   - grey  IDLE      — not paused, but the engine is not capturing (PTT mode, key not
 *                       held; no listening window open)
 *   - dim   N/A       — the speech pillar is absent (section null)
 * No per-key settings, so it uses the plain connection Property Inspector.
 */
@action({ UUID: "com.prosimcompanion.streamdeck.voicepause" })
export class VoicePauseAction extends StatusAction {
	protected render(tile: KeyTile, _settings: object, state: StatusDto, status: ConnStatus): void | Promise<void> {
		void tile.setTitle("");

		if (status !== "connected") {
			return void tile.setImage(
				keyTile({ background: Colors.idle, heading: "VOICE", value: "—", sub: connLabel(status), dim: true }),
			);
		}

		const voice = state.voice;
		if (!voice) {
			return void tile.setImage(
				keyTile({ background: Colors.disabled, heading: "VOICE", value: "N/A", sub: "no speech", dim: true }),
			);
		}

		if (voice.paused) {
			return void tile.setImage(
				keyTile({ background: Colors.error, heading: "VOICE", value: "PAUSED", sub: "press to resume" }),
			);
		}

		if (voice.listening) {
			return void tile.setImage(
				keyTile({ background: Colors.active, heading: "VOICE", value: "LISTENING", sub: "press to pause" }),
			);
		}

		return void tile.setImage(
			keyTile({ background: Colors.idle, heading: "VOICE", value: "IDLE", sub: "press to pause", dim: true }),
		);
	}

	override async onKeyDown(ev: KeyDownEvent): Promise<void> {
		const tile = ev.action as unknown as KeyTile;
		const res = await connection.post("speech.toggleListening");
		await showCommandResult(tile, res);
		// Don't wait up to a full poll period for the face to flip — refresh now.
		void connection.refresh();
	}
}
