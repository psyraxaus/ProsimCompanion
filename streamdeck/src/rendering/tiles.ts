import { ConnStatus, ServiceState } from "../connection/types";

/**
 * Dynamic key rendering. Stream Deck keys are drawn at runtime with
 * `setImage(<svg data uri>)`, so the plugin ships no per-state binary images —
 * one tile renderer covers callable/requested/active/completed/disabled.
 */

const SIZE = 144;

export const Colors = {
	callable: "#2f5d8a",
	requested: "#c98a00",
	active: "#2e7d32",
	completed: "#1b5e20",
	disabled: "#3a3a3a",
	idle: "#2a2a2a",
	ok: "#2e7d32",
	warn: "#c98a00",
	error: "#b00020",
	text: "#ffffff",
	subtext: "#c8c8c8",
};

export function stateColor(state: ServiceState | undefined): string {
	switch (state) {
		case "active":
			return Colors.active;
		case "completed":
			return Colors.completed;
		case "requested":
			return Colors.requested;
		case "callable":
			return Colors.callable;
		case "notAvailable":
		case "skipped":
		default:
			return Colors.disabled;
	}
}

/** Short uppercase label for a service state. */
export function stateShort(state: ServiceState | undefined): string {
	switch (state) {
		case "active":
			return "ACTIVE";
		case "completed":
			return "DONE";
		case "requested":
			return "REQ";
		case "callable":
			return "READY";
		case "skipped":
			return "SKIP";
		case "notAvailable":
			return "N/A";
		default:
			return "—";
	}
}

/** Short label for a non-connected connection status (shown on every key). */
export function connLabel(status: ConnStatus): string {
	switch (status) {
		case "connecting":
			return "connecting";
		case "reauth":
			return "re-pair";
		case "apiDisabled":
			return "API off";
		case "unconfigured":
			return "set up";
		default:
			return "offline";
	}
}

function escapeXml(s: string): string {
	return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

export interface TileOptions {
	/** Background fill. */
	background: string;
	/** Optional top heading (small). */
	heading?: string;
	/** Large central value/label (single line, auto-shrinks). Ignored when `lines` is set. */
	value?: string;
	/** Multi-line central text (smaller font, up to ~4 lines). Takes precedence over `value`. */
	lines?: string[];
	/** Optional bottom sub-line (small). */
	sub?: string;
	/** Optional coloured status dot in the top-right corner. */
	dot?: string;
	/** Dim the whole tile (e.g. disabled / not callable). */
	dim?: boolean;
}

/** Build a `data:image/svg+xml` URI for a key face. */
export function keyTile(opts: TileOptions): string {
	const { background, heading, value, lines, sub, dot, dim } = opts;
	const opacity = dim ? 0.45 : 1;

	const parts: string[] = [];
	parts.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${SIZE}" height="${SIZE}" viewBox="0 0 ${SIZE} ${SIZE}">`);
	parts.push(`<rect width="${SIZE}" height="${SIZE}" rx="16" fill="${background}" opacity="${opacity}"/>`);

	if (dot) {
		parts.push(`<circle cx="${SIZE - 18}" cy="18" r="9" fill="${dot}"/>`);
	}
	if (heading) {
		parts.push(
			`<text x="${SIZE / 2}" y="30" fill="${Colors.subtext}" font-family="Arial,Helvetica,sans-serif" font-size="18" font-weight="600" text-anchor="middle">${escapeXml(heading)}</text>`,
		);
	}
	if (lines && lines.length > 0) {
		const n = lines.length;
		const lineHeight = 24;
		const top = heading ? 44 : 30;
		const bottom = sub ? SIZE - 34 : SIZE - 12;
		const startY = (top + bottom) / 2 - ((n - 1) * lineHeight) / 2 + 7;
		lines.forEach((l, i) => {
			parts.push(
				`<text x="${SIZE / 2}" y="${startY + i * lineHeight}" fill="${Colors.text}" font-family="Arial,Helvetica,sans-serif" font-size="21" font-weight="600" text-anchor="middle">${escapeXml(l)}</text>`,
			);
		});
	} else if (value) {
		// Shrink the font as the value grows so long labels still fit.
		const fontSize = value.length > 9 ? 26 : value.length > 6 ? 32 : 40;
		const y = heading || sub ? 84 : 80;
		parts.push(
			`<text x="${SIZE / 2}" y="${y}" fill="${Colors.text}" font-family="Arial,Helvetica,sans-serif" font-size="${fontSize}" font-weight="700" text-anchor="middle">${escapeXml(value)}</text>`,
		);
	}
	if (sub) {
		parts.push(
			`<text x="${SIZE / 2}" y="${SIZE - 16}" fill="${Colors.subtext}" font-family="Arial,Helvetica,sans-serif" font-size="17" text-anchor="middle">${escapeXml(sub)}</text>`,
		);
	}
	parts.push(`</svg>`);

	return `data:image/svg+xml;charset=utf8,${encodeURIComponent(parts.join(""))}`;
}

/**
 * Three-row status tile for Connection Health: each row is a label with its own
 * coloured dot (per-connection green/red rather than one colour for the tile).
 */
export function healthTile(background: string, rows: { label: string; up: boolean }[]): string {
	const parts: string[] = [];
	parts.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${SIZE}" height="${SIZE}" viewBox="0 0 ${SIZE} ${SIZE}">`);
	parts.push(`<rect width="${SIZE}" height="${SIZE}" rx="16" fill="${background}"/>`);
	const n = Math.max(1, rows.length);
	const lineHeight = 34;
	const startY = SIZE / 2 - ((n - 1) * lineHeight) / 2;
	rows.forEach((r, i) => {
		const y = startY + i * lineHeight;
		parts.push(`<circle cx="34" cy="${y - 6}" r="9" fill="${r.up ? Colors.ok : Colors.error}"/>`);
		parts.push(
			`<text x="52" y="${y}" fill="${Colors.text}" font-family="Arial,Helvetica,sans-serif" font-size="22" font-weight="600" text-anchor="start">${escapeXml(r.label)}</text>`,
		);
	});
	parts.push(`</svg>`);
	return `data:image/svg+xml;charset=utf8,${encodeURIComponent(parts.join(""))}`;
}

/** Wrap a long string onto multiple short lines for a multi-line key value. */
export function wrap(text: string, perLine = 11, maxLines = 4): string[] {
	const words = text.split(/\s+/).filter((w) => w.length > 0);
	const lines: string[] = [];
	let line = "";
	for (const w of words) {
		if ((line + " " + w).trim().length > perLine && line) {
			if (lines.length >= maxLines - 1) {
				line = line.trim() + "…";
				break;
			}
			lines.push(line.trim());
			line = w;
		} else {
			line = (line + " " + w).trim();
		}
	}
	if (line && lines.length < maxLines) lines.push(line.trim());
	return lines.length > 0 ? lines : [text];
}

/** Turn a camelCase/PascalCase enum value into words: "taxiOut" → "Taxi Out". */
export function humanize(s: string): string {
	const spaced = s.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
	return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}
