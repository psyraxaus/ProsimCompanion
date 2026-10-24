/**
 * Cockpit-switch key faces (2026-10-24): Airbus Korry push buttons, toggles, rotary knobs and
 * guarded switches, drawn as SVG at runtime like every other key — own artwork from the A320
 * overhead, lights-out philosophy. A lit lamp is an outlined box in the lamp colour with the
 * legend in the same colour (no fill); an unlit lamp is a dark box with a dim legend. The
 * button bezel takes an optional identity colour (the hydraulic B / G / Y rings).
 *
 * The outer face edge is reserved for feedback: a short blue ring after a press that went
 * through, an amber ring plus a short reason when the app refused it.
 */

const SIZE = 144;
const FONT = "Arial Narrow, Arial, Helvetica, sans-serif";

export const Face = {
	background: "#101214",
	plate: "#1a1d21",
	bezel: "#3b4046",
	lampOff: "#0d0f11",
	legendOff: "#353a40",
	title: "#c9cdd2",
	knob: "#26292d",
	knobRidge: "#5a6066",
	knobEdge: "#4a4f55",
	lever: "#d9dde2",
	leverTip: "#eef0f2",
	guard: "#c0392b",
	guardShade: "#7d2219",
	flashOk: "#3ea6ff",
	flashBad: "#ffb000",
	offline: "#5c636b",
};

/**
 * Lamp colours when /api/controls carries none (the app's own defaults, CockpitSwitchRow):
 * Airbus amber for the upper FAULT/AVAIL lamp, lamp white for the lower ON/OFF lamp and the
 * active word of toggles, knobs and guarded switches.
 */
export const LampDefaults = {
	upper: "#ffb000",
	lower: "#f4f6f8",
};

export type Flash = "ok" | "bad" | undefined;

function esc(s: string): string {
	return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

function safeColor(c: string | undefined, fallback: string): string {
	return c && /^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/.test(c.trim()) ? c.trim() : fallback;
}

function text(x: number, y: number, size: number, fill: string, content: string, spacing = 1): string {
	return `<text x="${x}" y="${y}" text-anchor="middle" font-family="${FONT}" font-weight="700" font-size="${size}" letter-spacing="${spacing}" fill="${fill}">${esc(content)}</text>`;
}

/** The title strip: shrinks for long names, never wraps. */
function title(t: string): string {
	const s = t.trim().toUpperCase();
	const size = s.length > 13 ? 15 : s.length > 10 ? 17 : 21;
	return text(SIZE / 2, 31, size, Face.title, s);
}

function open(dim: boolean): string {
	return `<svg xmlns="http://www.w3.org/2000/svg" width="${SIZE}" height="${SIZE}" viewBox="0 0 ${SIZE} ${SIZE}"><rect width="${SIZE}" height="${SIZE}" rx="14" fill="${Face.background}"/><g opacity="${dim ? 0.45 : 1}">`;
}

function close(flash: Flash, note?: string): string {
	let out = `</g>`;
	if (flash) {
		out += `<rect x="2" y="2" width="${SIZE - 4}" height="${SIZE - 4}" rx="12" fill="none" stroke="${flash === "ok" ? Face.flashOk : Face.flashBad}" stroke-width="4"/>`;
	}
	if (note) {
		out += text(SIZE / 2, 138, 12, flash === "bad" ? Face.flashBad : Face.offline, note.toUpperCase().slice(0, 18));
	}
	return out + `</svg>`;
}

function plate(bezel: string | undefined, y = 44, h = 84): string {
	const stroke = safeColor(bezel, Face.bezel);
	const width = stroke === Face.bezel ? 2 : 3;
	return `<rect x="24" y="${y}" width="96" height="${h}" rx="6" fill="${Face.plate}" stroke="${stroke}" stroke-width="${width}"/>`;
}

function lamp(y: number, h: number, legend: string, color: string, lit: boolean, showUnlit: boolean, fontSize = 21): string {
	let out = `<rect x="30" y="${y}" width="84" height="${h}" rx="4" fill="${Face.lampOff}"/>`;
	const c = safeColor(color, LampDefaults.lower);
	if (lit) {
		out += `<rect x="31.5" y="${y + 1.5}" width="81" height="${h - 3}" rx="4" fill="none" stroke="${c}" stroke-width="3"/>`;
	}
	if (legend.trim().length > 0 && (lit || showUnlit)) {
		const size = legend.length > 6 ? Math.max(13, fontSize - 6) : fontSize;
		out += text(SIZE / 2, y + h / 2 + size * 0.36, size, lit ? c : Face.legendOff, legend.toUpperCase(), 1.5);
	}
	return out;
}

function uri(svg: string): string {
	return `data:image/svg+xml;charset=utf8,${encodeURIComponent(svg)}`;
}

export interface Lamp {
	legend: string;
	color: string;
	lit: boolean;
	/** Draw the legend dim while dark (default true); false = extinguished, nothing shown. */
	showUnlit?: boolean;
}

export interface KorryOptions {
	title: string;
	bezel?: string;
	/** Omit for a one-lamp Korry. */
	upper?: Lamp;
	lower: Lamp;
	dim?: boolean;
	flash?: Flash;
	note?: string;
}

/** Two-lamp (upper + lower) or one-lamp Korry push button. */
export function korryTile(o: KorryOptions): string {
	let svg = open(o.dim === true) + title(o.title) + plate(o.bezel);
	if (o.upper) {
		svg += lamp(50, 34, o.upper.legend, o.upper.color, o.upper.lit, o.upper.showUnlit !== false);
		svg += lamp(88, 34, o.lower.legend, o.lower.color, o.lower.lit, o.lower.showUnlit !== false);
	} else {
		svg += lamp(64, 44, o.lower.legend, o.lower.color, o.lower.lit, o.lower.showUnlit !== false);
	}
	return uri(svg + close(o.flash, o.note));
}

export interface ToggleOptions {
	title: string;
	bezel?: string;
	/** Lever up (first label) or down (second label). */
	up: boolean;
	labels: [string, string];
	activeColor: string;
	dim?: boolean;
	flash?: Flash;
	note?: string;
}

/** Two-position toggle: lever up or down, the active position word lit. */
export function toggleTile(o: ToggleOptions): string {
	const active = safeColor(o.activeColor, LampDefaults.lower);
	let svg = open(o.dim === true) + title(o.title) + plate(o.bezel);
	svg += text(SIZE / 2, 62, 15, o.up ? active : Face.legendOff, o.labels[0].toUpperCase(), 1.5);
	svg += text(SIZE / 2, 122, 15, o.up ? Face.legendOff : active, o.labels[1].toUpperCase(), 1.5);
	svg += `<circle cx="72" cy="92" r="14" fill="#2c3136" stroke="#474c53" stroke-width="2"/>`;
	const tipY = o.up ? 70 : 112;
	svg += `<line x1="72" y1="92" x2="72" y2="${tipY}" stroke="${Face.lever}" stroke-width="10" stroke-linecap="round"/>`;
	svg += `<circle cx="72" cy="${o.up ? 68 : 114}" r="8" fill="${Face.leverTip}"/>`;
	return uri(svg + close(o.flash, o.note));
}

export interface KnobOptions {
	title: string;
	bezel?: string;
	/** Position labels in catalogue order (2–5 shown around the knob). */
	positions: string[];
	/** Index of the current position, or -1 when unknown. */
	index: number;
	activeColor: string;
	dim?: boolean;
	flash?: Flash;
	note?: string;
}

/** Rotary selector: labels fan across the top, the pointer points at the current one. */
export function knobTile(o: KnobOptions): string {
	const active = safeColor(o.activeColor, LampDefaults.lower);
	const positions = o.positions.slice(0, 5);
	const n = Math.max(positions.length, 2);
	let svg = open(o.dim === true) + title(o.title) + plate(o.bezel);
	// Angles from -70° (left) to +70° (right), measured from straight up.
	const span = n === 2 ? 90 : 140;
	const start = -span / 2;
	const step = n > 1 ? span / (n - 1) : 0;
	const cx = 72;
	const cy = 96;
	positions.forEach((label, i) => {
		const a = ((start + i * step) * Math.PI) / 180;
		const lx = cx + Math.sin(a) * 44;
		const ly = cy - Math.cos(a) * 40 + 4;
		svg += text(lx, ly, 12, i === o.index ? active : Face.legendOff, label.toUpperCase().slice(0, 5), 1);
	});
	svg += `<circle cx="${cx}" cy="${cy}" r="26" fill="${Face.knob}" stroke="${Face.knobEdge}" stroke-width="2"/>`;
	svg += `<circle cx="${cx}" cy="${cy}" r="22" fill="none" stroke="${Face.knobRidge}" stroke-width="2" stroke-dasharray="3 4"/>`;
	if (o.index >= 0 && o.index < positions.length) {
		const a = ((start + o.index * step) * Math.PI) / 180;
		const px = cx + Math.sin(a) * 22;
		const py = cy - Math.cos(a) * 22;
		svg += `<line x1="${cx}" y1="${cy}" x2="${px.toFixed(1)}" y2="${py.toFixed(1)}" stroke="${active}" stroke-width="6" stroke-linecap="round"/>`;
	} else {
		svg += text(cx, cy + 5, 14, Face.legendOff, "?");
	}
	return uri(svg + close(o.flash, o.note));
}

export interface GuardedOptions {
	title: string;
	bezel?: string;
	/** True = the switch is in its "on"/non-zero position (guard drawn open). */
	on: boolean;
	labels: [string, string];
	activeColor: string;
	dim?: boolean;
	flash?: Flash;
	note?: string;
}

/** Guarded switch: red guard closed over the switch when off, lifted when on. */
export function guardedTile(o: GuardedOptions): string {
	const active = safeColor(o.activeColor, LampDefaults.lower);
	let svg = open(o.dim === true) + title(o.title) + plate(o.bezel);
	if (!o.on) {
		svg += `<rect x="32" y="54" width="80" height="66" rx="5" fill="${Face.guard}"/>`;
		svg += `<rect x="32" y="54" width="80" height="10" rx="3" fill="${Face.guardShade}"/>`;
		svg += `<line x1="46" y1="72" x2="46" y2="104" stroke="#8e2a20" stroke-width="2"/>`;
		svg += `<line x1="98" y1="72" x2="98" y2="104" stroke="#8e2a20" stroke-width="2"/>`;
	} else {
		svg += `<rect x="32" y="48" width="80" height="12" rx="3" fill="${Face.guard}"/>`;
		svg += `<rect x="32" y="48" width="80" height="4" rx="2" fill="${Face.guardShade}"/>`;
		svg += text(SIZE / 2, 76, 15, active, o.labels[0].toUpperCase(), 1.5);
		svg += text(SIZE / 2, 124, 15, Face.legendOff, o.labels[1].toUpperCase(), 1.5);
		svg += `<circle cx="72" cy="98" r="12" fill="#2c3136" stroke="#474c53" stroke-width="2"/>`;
		svg += `<line x1="72" y1="98" x2="72" y2="84" stroke="${Face.lever}" stroke-width="9" stroke-linecap="round"/>`;
		svg += `<circle cx="72" cy="83" r="7" fill="${Face.leverTip}"/>`;
	}
	return uri(svg + close(o.flash, o.note));
}

/** A plain plate with a dash — unpaired, offline, or no such switch. */
export function blankTile(t: string, note: string, dim = true): string {
	let svg = open(dim) + title(t) + plate(undefined);
	svg += text(SIZE / 2, 96, 24, Face.offline, "—");
	return uri(svg + close(undefined, note));
}

/** The Camera view key: a lens with the view name. */
export function cameraTile(name: string, note?: string, dim = false, flash?: Flash): string {
	let svg = open(dim) + title("CAMERA") + plate(undefined);
	svg += `<circle cx="72" cy="78" r="18" fill="#0d0f11" stroke="#474c53" stroke-width="3"/>`;
	svg += `<circle cx="72" cy="78" r="8" fill="#2c3136" stroke="#5fb4ff" stroke-width="2"/>`;
	svg += `<rect x="60" y="54" width="24" height="7" rx="2" fill="#474c53"/>`;
	const label = name.trim().length > 0 ? name.trim() : "—";
	svg += text(SIZE / 2, 118, label.length > 9 ? 14 : 17, "#f4f6f8", label.toUpperCase(), 1);
	return uri(svg + close(flash, note));
}
