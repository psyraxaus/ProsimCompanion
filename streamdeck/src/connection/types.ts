/**
 * Shared types for the ProsimCompanion connection layer.
 *
 * The wire contract is REST-only against ProsimCompanion's embedded web server:
 *   GET  /api/status          — polled snapshot of phase / connections / gsx / checklist
 *   POST /api/command/{name}  — semantic commands; response `{ outcome, reason }`
 * Property names are camelCase; enums ride as camelCase strings.
 */

/** Connection settings — persisted as Stream Deck *global* settings so every key shares one connection. */
export type ConnectionSettings = {
	host: string;
	port: number;
	token: string;
};

export const DEFAULT_SETTINGS: ConnectionSettings = {
	host: "127.0.0.1",
	port: 5320,
	token: "",
};

/**
 * Lifecycle of the single shared connection.
 *
 * "apiDisabled" is distinct from plain connection failure: the server answered
 * but /api/status returned 404, which means `commandApi.enabled` is off in
 * ProsimCompanion's settings. "reauth" means the token was rejected (HTTP 401)
 * and the user must re-pair.
 */
export type ConnStatus = "unconfigured" | "connecting" | "connected" | "reauth" | "apiDisabled" | "disconnected";

/** GSX service lifecycle (camelCase string values from /api/status). */
export type ServiceState = "notAvailable" | "callable" | "requested" | "active" | "completed" | "skipped";

/** One entry of `gsx.services[]` in the status DTO. */
export interface ServiceStatusDto {
	type?: string;
	state?: ServiceState;
	detail?: string;
}

/** The `gsx` section of /api/status. May be null when GSX is absent. */
export interface GsxStatusDto {
	automationActive?: boolean;
	nextService?: string;
	services?: ServiceStatusDto[];
	refuelPercent?: number;
	paxBoarded?: number;
	paxTotal?: number;
}

/** The `connections` section of /api/status. May be null. */
export interface ConnectionsStatusDto {
	prosim?: boolean;
	msfs?: boolean;
	gsx?: boolean;
}

/** The `checklist` section of /api/status. May be null when no checklist is active. */
export interface ChecklistStatusDto {
	name?: string;
	item?: string;
	index?: number;
	count?: number;
}

/** The whole /api/status payload. Every section is optional — degrade, not fail. */
export interface StatusDto {
	phase?: string;
	connections?: ConnectionsStatusDto | null;
	gsx?: GsxStatusDto | null;
	checklist?: ChecklistStatusDto | null;
}

/** Camel-case outcome enum returned by POST /api/command/{name}. */
export type CommandOutcome =
	| "success"
	| "alreadySatisfied"
	| "phaseMismatch"
	| "preconditionFailed"
	| "failed"
	| "unavailable";

/** Result of a command POST. `ok` means the key should flash the OK checkmark. */
export interface PostResult {
	ok: boolean;
	status: number;
	outcome?: CommandOutcome | string;
	reason?: string;
}
