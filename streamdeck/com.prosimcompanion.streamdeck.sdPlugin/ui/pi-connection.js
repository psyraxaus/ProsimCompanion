/*
 * Renders + wires the shared "ProsimCompanion Connection" form (host / port /
 * token / pair-by-URL) into a container, bound to GLOBAL settings via
 * window.PI. Used by every Property Inspector so the one connection is
 * configured from any key.
 */
function renderConnectionForm(container) {
	container.innerHTML =
		'<div class="group">' +
		'<div class="title">ProsimCompanion Connection</div>' +
		'<label for="cf-host">Host</label><input id="cf-host" type="text" placeholder="127.0.0.1">' +
		'<label for="cf-port">Port</label><input id="cf-port" type="number" placeholder="5320">' +
		'<label for="cf-token">Token</label><input id="cf-token" type="password" placeholder="paste bearer token">' +
		'<label for="cf-pair">Pair via onboarding URL / QR</label>' +
		'<input id="cf-pair" type="text" placeholder="http://host:5320/?token=...">' +
		'<div class="status" id="cf-hint">Paste the token, or the onboarding URL / QR link from the ProsimCompanion web UI.</div>' +
		"</div>";

	const host = container.querySelector("#cf-host");
	const port = container.querySelector("#cf-port");
	const token = container.querySelector("#cf-token");
	const pair = container.querySelector("#cf-pair");
	const hint = container.querySelector("#cf-hint");

	function setHint(text, cls) {
		hint.textContent = text;
		hint.className = "status" + (cls ? " " + cls : "");
	}

	window.PI.onGlobal(function (g) {
		host.value = g.host || "";
		port.value = g.port || "";
		token.value = g.token || "";
		if (g.token && g.host) setHint("Configured for " + g.host + ":" + (g.port || 5320) + ".", "ok");
	});

	host.addEventListener("change", function () {
		window.PI.setGlobal({ host: host.value.trim() || "127.0.0.1" });
	});
	port.addEventListener("change", function () {
		window.PI.setGlobal({ port: parseInt(port.value, 10) || 5320 });
	});
	token.addEventListener("change", function () {
		window.PI.setGlobal({ token: token.value.trim() });
	});
	pair.addEventListener("change", function () {
		const raw = pair.value.trim();
		if (!raw) return;
		const parsed = window.parsePairingUrl(raw);
		if (parsed) {
			host.value = parsed.host;
			port.value = parsed.port;
			token.value = parsed.token;
			window.PI.setGlobal(parsed);
			pair.value = "";
			setHint("Paired with " + parsed.host + ":" + parsed.port + ".", "ok");
		} else {
			setHint("Could not read host/port/token from that URL.", "err");
		}
	});
}
