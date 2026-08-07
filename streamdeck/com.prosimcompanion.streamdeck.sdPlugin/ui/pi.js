/*
 * Self-contained Property Inspector helper for the ProsimCompanion plugin.
 *
 * Implements the Stream Deck PI handshake directly (no external component
 * library / CDN) so it works offline on the sim PC and is independent of any
 * sdpi-components version. Exposes a tiny window.PI API the per-action HTML
 * pages use to bind fields to GLOBAL settings (host/port/token, shared by all
 * keys) and per-ACTION settings (the function dropdowns).
 */
(function () {
	let ws;
	let uuid;
	let actionInfo;
	let globalSettings = {};
	let settings = {};

	const readyCbs = [];
	const globalCbs = [];
	const settingsCbs = [];

	function fire(cbs, arg) {
		for (const cb of cbs) {
			try {
				cb(arg);
			} catch (e) {
				console.error(e);
			}
		}
	}

	window.connectElgatoStreamDeckSocket = function (inPort, inUUID, inRegisterEvent, _inInfo, inActionInfo) {
		uuid = inUUID;
		try {
			actionInfo = JSON.parse(inActionInfo);
			settings = (actionInfo && actionInfo.payload && actionInfo.payload.settings) || {};
		} catch (e) {
			settings = {};
		}

		ws = new WebSocket("ws://127.0.0.1:" + inPort);
		ws.onopen = function () {
			ws.send(JSON.stringify({ event: inRegisterEvent, uuid: inUUID }));
			ws.send(JSON.stringify({ event: "getGlobalSettings", context: uuid }));
			fire(readyCbs, settings);
			fire(settingsCbs, settings);
		};
		ws.onmessage = function (evt) {
			let msg;
			try {
				msg = JSON.parse(evt.data);
			} catch (e) {
				return;
			}
			if (msg.event === "didReceiveGlobalSettings") {
				globalSettings = (msg.payload && msg.payload.settings) || {};
				fire(globalCbs, globalSettings);
			} else if (msg.event === "didReceiveSettings") {
				settings = (msg.payload && msg.payload.settings) || {};
				fire(settingsCbs, settings);
			}
		};
	};

	window.PI = {
		onReady: function (cb) {
			readyCbs.push(cb);
		},
		onGlobal: function (cb) {
			globalCbs.push(cb);
		},
		onSettings: function (cb) {
			settingsCbs.push(cb);
		},
		getGlobal: function () {
			return globalSettings;
		},
		getSettings: function () {
			return settings;
		},
		setGlobal: function (patch) {
			globalSettings = Object.assign({}, globalSettings, patch);
			if (ws && ws.readyState === 1) {
				ws.send(JSON.stringify({ event: "setGlobalSettings", context: uuid, payload: globalSettings }));
			}
		},
		setSettings: function (patch) {
			settings = Object.assign({}, settings, patch);
			if (ws && ws.readyState === 1) {
				ws.send(JSON.stringify({ event: "setSettings", context: uuid, payload: settings }));
			}
		},
	};

	/**
	 * Parse a ProsimCompanion onboarding URL: http://{host}:{port}/?token={token}
	 * (token in the query string; a legacy #token= fragment is accepted too).
	 * Returns { host, port, token } or null.
	 */
	window.parsePairingUrl = function (raw) {
		try {
			const u = new URL(String(raw).trim());
			const host = u.hostname;
			const port = u.port ? parseInt(u.port, 10) : 5320;
			let token = u.searchParams.get("token") || "";
			if (!token) {
				const hash = u.hash && u.hash.charAt(0) === "#" ? u.hash.slice(1) : u.hash || "";
				token = new URLSearchParams(hash).get("token") || "";
			}
			if (!token) return null;
			return { host: host, port: port, token: token };
		} catch (e) {
			return null;
		}
	};
})();
