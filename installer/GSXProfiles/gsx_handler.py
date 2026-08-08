# ProsimCompanion GSX handler script.
#
# Loaded by GSX Pro v4.0.0+ as a per-aircraft (tier 3) handler script,
# installed to %APPDATA%\Virtuali\Airplanes\<profile>\gsx_handler.py.
# Ported from the Prosim2GSX handler (same hooks, same wire contract);
# only the app endpoint and identifiers changed.
#
# v4.0.0 applies overrides from TOP-LEVEL functions/variables only: a
# function whose name does not start with "_" is bound onto the handler
# instance with `self` injected, like a normal method. The pre-v4
# `class ... / handler = Instance()` pattern is NOT applied by v4 (the
# loader would only see the class + instance as top-level names, never
# the methods), so it is deliberately not used here.
#
# Two responsibilities:
#  1. Event bridge: push lifecycle/service hook events to ProsimCompanion
#     so the app gets precise timing (Flight Status "Last Handler Event"
#     + the session event log). Each hook runs the built-in (_super_)
#     logic first — guarded so we never suppress real stock behaviour —
#     then fire-and-forget reports the event. The sandbox has no HTTP
#     POST, so events are GET-encoded.
#  2. VDGS flight display: render the flight identity on the gate's VDGS
#     via addVdgsMessage() (replace-by-id, re-pushed on engage/boarding/
#     departure, cleared on disengage).
#
# Constraints: no 'import' beyond what GSX provides, no file I/O, no
# threading. GSX-provided globals only: fetchJson, showMessage,
# hasStockBehavior, getattr, addVdgsMessage, removeVdgsMessage, runAsync,
# wait, executeCalculatorCode.
#
# Endpoints (loopback, no token needed — loopback is always exempt):
#   GET /api/gsxmenu/events?e=&r=&ts= -> null  (event push)
#   GET /api/gsxmenu/flight-info      -> {callsign,...} | null
#
# If you've changed ProsimCompanion's web port from the default 5320,
# edit PROSIMCOMPANION_PORT below to match (ProsimCompanion rewrites
# this line automatically at startup).

PROSIMCOMPANION_PORT = 5320
PROSIMCOMPANION_BASE = "http://127.0.0.1:" + str(PROSIMCOMPANION_PORT) + "/api/gsxmenu"

# Loopback fetch budget (seconds). The endpoints are local and instant;
# cap it low so a missing or hung ProsimCompanion degrades gracefully
# instead of blocking the GSX tasklet that called the handler hook.
_FETCH_TIMEOUT = 2


def _emit(event, reason=None):
    # Fire-and-forget. Event names and GSX reason tokens are fixed
    # url-safe identifiers, so no escaping is needed. Swallow everything:
    # a reporting failure must never disrupt a ground operation.
    try:
        url = PROSIMCOMPANION_BASE + "/events?e=" + event
        if reason:
            url = url + "&r=" + str(reason)
        fetchJson(url, timeout=_FETCH_TIMEOUT)
    except Exception as ex:
        print("[ProsimCompanion] event emit failed (" + str(event) + "): " + str(ex))


# ── VDGS flight display ────────────────────────────────────────────────

_VDGS_MSG_ID = "prosimcompanion_flight"


def _clip(s, n):
    return (s or "")[:n]


def _build_vdgs_message(info):
    cs = (info.get("callsign") or "").strip()
    fn = (info.get("flightNumber") or "").strip()
    o = (info.get("origin") or "").strip()
    d = (info.get("destination") or "").strip()
    ident = cs or fn or "PROSIM"
    route = (o + "-" + d) if (o and d) else (d or o or "----")
    return {
        "id": _VDGS_MSG_ID,
        "display": {
            "narrow": {"pages": [{
                "lines": ["FLT", _clip(ident, 6), "DEST", _clip(d, 6) or "----"],
                "duration": 5000}]},
            "wide": {"pages": [{
                "lines": ["FLIGHT", _clip(ident, 9), "ROUTE", _clip(route, 9)],
                "duration": 5000}]},
            "x": {"pages": [{
                "lines": ["FLIGHT", _clip(ident, 10), "ROUTE", _clip(route, 10)],
                "duration": 5000}]},
        },
    }


def _push_flight_info():
    try:
        info = fetchJson(PROSIMCOMPANION_BASE + "/flight-info", timeout=_FETCH_TIMEOUT)
    except Exception as ex:
        print("[ProsimCompanion] flight-info fetch failed: " + str(ex))
        return
    if not info:
        # No OFP loaded (or nothing meaningful yet) — clear any stale page.
        try:
            removeVdgsMessage(_VDGS_MSG_ID)
        except Exception:
            pass
        return
    try:
        addVdgsMessage(_build_vdgs_message(info))
    except Exception as ex:
        print("[ProsimCompanion] addVdgsMessage failed: " + str(ex))


def _clear_flight_info():
    try:
        removeVdgsMessage(_VDGS_MSG_ID)
    except Exception:
        pass


def _run_super(self, name, *args):
    # Run the built-in implementation only when the stock handler actually
    # has one (many hooks are just `pass`). Preserves real behaviour such
    # as PMDG/Fenix door automation that lives in onBoardingRequested etc.
    if hasStockBehavior(name):
        getattr(self, "_super_" + name)(*args)


# ── Aircraft engage / disengage ────────────────────────────────────────

def onAircraftEngaged(self):
    _run_super(self, 'onAircraftEngaged')
    _push_flight_info()
    _emit('aircraftEngaged')


def onAircraftDisengaged(self):
    _run_super(self, 'onAircraftDisengaged')
    _clear_flight_info()
    _emit('aircraftDisengaged')


# ── Event bridge ───────────────────────────────────────────────────────

def onGateReset(self, reason):
    _run_super(self, 'onGateReset', reason)
    _emit('gateReset', reason)


def onBoardingRequested(self):
    _run_super(self, 'onBoardingRequested')
    _push_flight_info()
    _emit('boardingRequested')


def onDeboardingRequested(self):
    _run_super(self, 'onDeboardingRequested')
    _emit('deboardingRequested')


def onRefuelingRequested(self):
    _run_super(self, 'onRefuelingRequested')
    _emit('refuelingRequested')


def onCateringRequested(self):
    _run_super(self, 'onCateringRequested')
    _emit('cateringRequested')


def onDepartureRequested(self):
    _run_super(self, 'onDepartureRequested')
    _push_flight_info()
    _emit('departureRequested')


def onJetwayConnected(self):
    _run_super(self, 'onJetwayConnected')
    _emit('jetwayConnected')


def onJetwayDisconnected(self):
    _run_super(self, 'onJetwayDisconnected')
    _emit('jetwayDisconnected')


def onBypassPinConnected(self):
    _run_super(self, 'onBypassPinConnected')
    _emit('bypassPinConnected')


def onBypassPinDisconnected(self):
    _run_super(self, 'onBypassPinDisconnected')
    _emit('bypassPinDisconnected')


def onDeicingAction(self):
    _run_super(self, 'onDeicingAction')
    _emit('deicingAction')
