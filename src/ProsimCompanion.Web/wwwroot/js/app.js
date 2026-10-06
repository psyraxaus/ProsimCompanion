// ProsimCompanion web UI client-side behaviours. Everything here is presentation-only and
// deliberately runs in the browser so nothing animates over the Blazor Server circuit:
//   1. <split-flap> custom element — the Solari display ported from the predecessor's WPF
//      SplitFlapCharacterControl (same drum, min-flip count, deceleration zone, per-tick
//      squeeze). Blazor only sets the `text` attribute; UTC clock modes self-update.
//   2. Tab bar overflow chevrons + keeping the active tab scrolled into view.
//   3. Circuit auto-recovery — remote tablets (iPad Safari) drop the SignalR WebSocket on
//      screen lock / app switch; recover without user action instead of sitting stale.

(function () {
  "use strict";

  // ------------------------------------------------------------- blazor helpers
  // Tiny interop surface for pages that need the browser to do a layout-aware thing.
  // scrollIntoView keeps the active checklist entry / the current ECAM line visible while
  // the lists scroll inside their cards (ADR-0011, owner request 2026-09-20).
  window.prosimCompanion = window.prosimCompanion || {};
  window.prosimCompanion.scrollIntoView = function (id) {
    const el = document.getElementById(id);
    if (el) el.scrollIntoView({ block: "nearest", behavior: "smooth" });
  };

  // Screen keep-awake (issue #150, ADR-0013): the Screen Wake Lock API, a per-DEVICE choice
  // kept in this browser's localStorage — whether a tablet's screen stays on is that tablet's
  // business, not a server setting. The lock exists only in a secure context (https://, or
  // http://localhost), so over http://<lan-ip> the state reads "needs-https" and the
  // Appearance page points at the HTTPS setup. The browser drops the lock whenever the page is
  // hidden; it is re-acquired on visibilitychange and released on unload.
  const WAKE_KEY = "prosimCompanion.keepAwake";
  let wakeSentinel = null;
  let wakeWanted = false;
  let wakeListeners = [];
  // Diagnostics for the Appearance card (2026-10-03 iPad report: "the screen still sleeps"):
  // the last refusal and the last release, with their times, so a failed test explains itself.
  let wakeLastError = null;
  let wakeLastErrorAt = null;
  let wakeReleasedAt = null;
  let wakeAcquiredAt = null;
  let wakeAttempts = 0;

  function clock() {
    const d = new Date();
    return d.toISOString().substring(11, 19) + "Z";
  }

  // The geometry facts for the Appearance card (iPad home-screen app, owner screenshots
  // 2026-10-03: black bands above the header and below the footer). One line that says what
  // the web view was given versus the screen, and what the safe-area insets read.
  function viewportFacts() {
    const probe = document.createElement("div");
    probe.style.cssText = "position:fixed;top:0;left:0;visibility:hidden;pointer-events:none;"
      + "padding-top:env(safe-area-inset-top,0px);padding-bottom:env(safe-area-inset-bottom,0px);";
    document.body.appendChild(probe);
    const cs = getComputedStyle(probe);
    const facts = {
      innerWidth: window.innerWidth,
      innerHeight: window.innerHeight,
      outerHeight: window.outerHeight,
      screenWidth: screen.width,
      screenHeight: screen.height,
      visualHeight: window.visualViewport ? Math.round(window.visualViewport.height) : null,
      dpr: window.devicePixelRatio,
      safeTop: cs.paddingTop,
      safeBottom: cs.paddingBottom,
      displayMode: (window.matchMedia && window.matchMedia("(display-mode: standalone)").matches) ? "standalone"
        : (window.matchMedia && window.matchMedia("(display-mode: fullscreen)").matches) ? "fullscreen" : "browser",
      appHeight: (document.querySelector(".app") || {}).offsetHeight || null,
    };
    probe.remove();
    return facts;
  }

  function wakeState() {
    if (!wakeWanted) return "off";
    if (!window.isSecureContext) return "needs-https";
    if (!("wakeLock" in navigator)) return "not-supported";
    return wakeSentinel && !wakeSentinel.released ? "active" : "requesting";
  }

  function notifyWake() {
    const state = wakeState();
    wakeListeners.forEach((ref) => {
      try { ref.invokeMethodAsync("OnWakeLockState", state); } catch (_) { /* circuit gone */ }
    });
  }

  async function acquireWake() {
    if (!wakeWanted || !window.isSecureContext || !("wakeLock" in navigator)) { notifyWake(); return; }
    if (wakeSentinel && !wakeSentinel.released) { notifyWake(); return; }
    if (document.visibilityState !== "visible") { notifyWake(); return; } // the browser would refuse anyway
    wakeAttempts++;
    try {
      wakeSentinel = await navigator.wakeLock.request("screen");
      wakeAcquiredAt = clock();
      wakeLastError = null;
      wakeSentinel.addEventListener("release", () => {
        // The browser let go: page hidden, tab switched, Low Power Mode, or iPadOS auto-lock
        // overriding the lock. Recorded so the card can show WHEN it happened.
        wakeReleasedAt = clock();
        notifyWake();
      });
    } catch (e) {
      // NotAllowedError = Low Power Mode or a browser rule (Safari may want a user gesture
      // first — see the first-touch retry in initWakeLock); the text is shown on the card.
      wakeSentinel = null;
      wakeLastError = (e && e.name ? e.name : "Error") + (e && e.message ? ": " + e.message : "");
      wakeLastErrorAt = clock();
    }
    notifyWake();
  }

  async function releaseWake() {
    if (wakeSentinel) {
      try { await wakeSentinel.release(); } catch (_) { /* already gone */ }
      wakeSentinel = null;
    }
    notifyWake();
  }

  window.prosimCompanion.wakeLock = {
    // What this device knows: the stored preference, the current state, and the facts the
    // Appearance page shows ("Safari on iPad · https · standalone").
    describe: function () {
      return {
        wanted: wakeWanted,
        state: wakeState(),
        secure: !!window.isSecureContext,
        supported: "wakeLock" in navigator,
        standalone: !!(window.matchMedia && window.matchMedia("(display-mode: standalone)").matches) || window.navigator.standalone === true,
        scheme: location.protocol.replace(":", ""),
        userAgent: navigator.userAgent,
        lastError: wakeLastError,
        lastErrorAt: wakeLastErrorAt,
        acquiredAt: wakeAcquiredAt,
        releasedAt: wakeReleasedAt,
        attempts: wakeAttempts,
        viewport: viewportFacts(),
      };
    },
    setWanted: function (wanted) {
      wakeWanted = !!wanted;
      try { localStorage.setItem(WAKE_KEY, wakeWanted ? "1" : "0"); } catch (_) { /* private mode */ }
      if (wakeWanted) acquireWake(); else releaseWake();
    },
    // The card's "Request again" button: a request from a real user gesture.
    retry: function () { acquireWake(); },
    subscribe: function (dotNetRef) {
      wakeListeners.push(dotNetRef);
      notifyWake();
    },
    unsubscribe: function (dotNetRef) {
      wakeListeners = wakeListeners.filter((r) => r !== dotNetRef);
    },
  };

  function initWakeLock() {
    try { wakeWanted = localStorage.getItem(WAKE_KEY) === "1"; } catch (_) { wakeWanted = false; }
    if (wakeWanted) acquireWake();
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible" && wakeWanted) acquireWake();
    });
    // iPadOS Safari and the home-screen app: a request made at load, before any touch, can
    // be refused; the first user gesture on the page retries it (every page, not only
    // Appearance — the user may never open Appearance in flight).
    const retryOnGesture = () => { if (wakeWanted && !(wakeSentinel && !wakeSentinel.released)) acquireWake(); };
    document.addEventListener("pointerdown", retryOnGesture, { passive: true });
    document.addEventListener("touchend", retryOnGesture, { passive: true });
    window.addEventListener("focus", retryOnGesture);
    window.addEventListener("pageshow", retryOnGesture);
    window.addEventListener("pagehide", () => { if (wakeSentinel) wakeSentinel.release().catch(() => {}); });
  }

  // Flight Monitor board (owner decision 2026-09-23): the board is a fixed 1920×1080 stage
  // scaled as ONE piece to fit the window and centred — never reflowed, so nothing can
  // overlap at any window size. Viewport units and CSS zoom fight each other; a transform
  // does not.
  window.prosimCompanion.fitStage = function (id, width, height) {
    const el = document.getElementById(id);
    if (!el) return;
    const fit = () => {
      const k = Math.min(window.innerWidth / width, window.innerHeight / height);
      el.style.transformOrigin = "top left";
      el.style.transform = "translate(" + ((window.innerWidth - width * k) / 2) + "px, "
        + ((window.innerHeight - height * k) / 2) + "px) scale(" + k + ")";
    };
    fit();
    if (el._fitStage) window.removeEventListener("resize", el._fitStage);
    el._fitStage = fit;
    window.addEventListener("resize", fit);
  };

  // Clipboard for the Logs page "Copy version" button (users paste the banner into GitHub
  // issues). navigator.clipboard needs a secure context; a LAN http:// tablet falls back to
  // the legacy execCommand path. Returns whether the copy succeeded.
  window.prosimCompanion.copyText = async function (text) {
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
        return true;
      }
    } catch (e) { /* fall through */ }
    try {
      const area = document.createElement("textarea");
      area.value = text;
      area.setAttribute("readonly", "");
      area.style.position = "fixed";
      area.style.opacity = "0";
      document.body.appendChild(area);
      area.select();
      const ok = document.execCommand("copy");
      document.body.removeChild(area);
      return ok;
    } catch (e) {
      return false;
    }
  };

  // Opens (or focuses) the pop-out Flight Monitor window. Same origin, so the onboarded
  // browser cookie carries over; a popup that the browser blocked falls back to a tab.
  window.prosimCompanion.openMonitor = function () {
    const url = "monitor";
    const win = window.open(url, "prosim-flight-monitor", "popup=yes,width=1600,height=900");
    if (win) { win.focus(); return true; }
    window.open(url, "_blank");
    return false;
  };

  // Flight Status tile drag (issue #160). Pointer events, not HTML5 drag-and-drop: the latter
  // never fires on iPad touch. The DOM is NOT reordered here — Blazor owns it; the drop goes
  // to .NET (SwapTiles) and the page re-renders in the new order. While dragging, a ghost copy
  // of the tile follows the pointer and the tile under it lights up as the drop target; the
  // drop swaps the two tiles.
  // Near the top / bottom of the window the page auto-scrolls so a tile can
  // travel further than one screen on a tablet.
  // The whole tile is the handle (owner pick 2026-10-07, option A). Mouse: press anywhere and
  // the drag starts on the first few pixels of movement. Finger / pen: hold still for
  // HOLD_MS, then drag — a short swipe before the hold is up stays a page scroll, because the
  // tiles fill a tablet's screen in edit mode and there would be nothing else to scroll by.
  // Once a touch drag is live the non-passive touchmove handler stops the browser taking the
  // gesture for scrolling (which would end the pointer stream with pointercancel). The grip
  // button still works and starts at once on any pointer (it has touch-action: none).
  const tileDrags = new Map(); // container id → teardown
  const HOLD_MS = 300;
  const MOUSE_SLOP_PX = 4;
  const TOUCH_SLOP_PX = 10;

  window.prosimCompanion.tileDrag = {
    attach: function (containerId, dotNetRef) {
      this.detach(containerId);
      const container = document.getElementById(containerId);
      if (!container) return;

      let drag = null;    // { id, handle, ghost, dx, dy, x, y, target, raf }
      let pending = null; // a press that may become a drag: { tile, x, y, pointerId, mouse, timer }

      const clearTarget = () => {
        if (drag && drag.target) {
          drag.target.classList.remove("drop-target");
          drag.target = null;
        }
      };

      // One target, no edge (owner decision 2026-10-07): the tile under the pointer lights
      // up as a whole and the drop swaps the two tiles. A before/after edge bar made a drop
      // land one place off when the pointer sat near the middle of the target.
      const locate = () => {
        if (!drag) return;
        drag.ghost.style.transform = "translate(" + (drag.x - drag.dx) + "px, " + (drag.y - drag.dy) + "px)";
        const under = document.elementFromPoint(drag.x, drag.y);
        const tile = under ? under.closest(".fs-tile") : null;
        const valid = tile && tile.parentElement === container && tile.dataset.tile !== drag.id;
        if (!valid) { clearTarget(); return; }
        if (tile !== drag.target) {
          clearTarget();
          drag.target = tile;
          tile.classList.add("drop-target");
        }
      };

      const tick = () => {
        if (!drag) return;
        const edge = 56;
        const h = window.innerHeight;
        if (drag.y < edge) window.scrollBy(0, -Math.ceil((edge - drag.y) / 4));
        else if (drag.y > h - edge) window.scrollBy(0, Math.ceil((drag.y - (h - edge)) / 4));
        locate();
        drag.raf = requestAnimationFrame(tick);
      };

      const finish = (drop) => {
        if (!drag) return;
        const d = drag;
        drag = null;
        cancelAnimationFrame(d.raf);
        d.ghost.remove();
        const source = container.querySelector('.fs-tile[data-tile="' + d.id + '"]');
        if (source) source.classList.remove("dragging");
        if (d.target) d.target.classList.remove("drop-target");
        if (drop && d.target) {
          dotNetRef.invokeMethodAsync("SwapTiles", d.id, d.target.dataset.tile)
            .catch(() => { /* circuit gone — the page reloads on its own */ });
        }
      };

      const clearPending = () => {
        if (pending) { clearTimeout(pending.timer); pending = null; }
      };

      // Starts the drag from the press recorded in `pending`: the ghost is positioned so the
      // tile keeps its offset under the pointer, as if lifted in place.
      const begin = () => {
        const p = pending;
        pending = null;
        clearTimeout(p.timer);
        const tile = p.tile;
        if (!tile.isConnected) return;
        const r = tile.getBoundingClientRect();
        const ghost = tile.cloneNode(true);
        ghost.classList.add("fs-tile-ghost");
        ghost.style.width = r.width + "px";
        ghost.style.height = r.height + "px";
        document.body.appendChild(ghost);
        tile.classList.add("dragging");
        drag = { id: tile.dataset.tile, handle: tile, ghost, dx: p.x - r.left, dy: p.y - r.top,
          x: p.x, y: p.y, target: null, raf: 0 };
        try { tile.setPointerCapture(p.pointerId); } catch (_) { /* already released */ }
        drag.raf = requestAnimationFrame(tick);
      };

      const onDown = (e) => {
        if (drag || pending || (e.pointerType === "mouse" && e.button !== 0)) return;
        const tile = e.target.closest(".fs-tile");
        if (!tile || tile.parentElement !== container) return;
        const grip = !!e.target.closest(".fs-tile-grip");
        // The arrows stay buttons; everything else on the tile is the handle (the card
        // content has pointer-events: none in edit mode).
        if (!grip && e.target.closest("button")) return;
        const mouse = e.pointerType === "mouse";
        pending = { tile, x: e.clientX, y: e.clientY, pointerId: e.pointerId, mouse, timer: 0 };
        if (grip) { e.preventDefault(); begin(); return; }
        if (mouse) { e.preventDefault(); return; }         // starts on the first movement
        pending.timer = setTimeout(begin, HOLD_MS);         // finger / pen: hold, then drag
      };
      const onMove = (e) => {
        if (drag) { drag.x = e.clientX; drag.y = e.clientY; return; }
        if (!pending || e.pointerId !== pending.pointerId) return;
        const moved = Math.hypot(e.clientX - pending.x, e.clientY - pending.y);
        if (pending.mouse) { if (moved >= MOUSE_SLOP_PX) begin(); }
        else if (moved >= TOUCH_SLOP_PX) clearPending();     // a swipe: let the page scroll
      };
      const onUp = () => { clearPending(); finish(true); };
      const onCancel = () => { clearPending(); finish(false); };
      const onTouchMove = (e) => { if (drag) e.preventDefault(); };
      const onContextMenu = (e) => { if (drag || pending) e.preventDefault(); }; // long-press menu

      container.addEventListener("pointerdown", onDown);
      container.addEventListener("pointermove", onMove);
      container.addEventListener("pointerup", onUp);
      container.addEventListener("pointercancel", onCancel);
      container.addEventListener("touchmove", onTouchMove, { passive: false });
      container.addEventListener("contextmenu", onContextMenu);
      tileDrags.set(containerId, () => {
        clearPending();
        finish(false);
        container.removeEventListener("pointerdown", onDown);
        container.removeEventListener("pointermove", onMove);
        container.removeEventListener("pointerup", onUp);
        container.removeEventListener("pointercancel", onCancel);
        container.removeEventListener("touchmove", onTouchMove);
        container.removeEventListener("contextmenu", onContextMenu);
      });
    },
    detach: function (containerId) {
      const teardown = tileDrags.get(containerId);
      if (teardown) { teardown(); tileDrags.delete(containerId); }
    },
  };

  // ------------------------------------------------------------------ split-flap

  const DRUM = " ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789:-/";
  const MIN_FLIPS = 5;
  const FAST_TICK_MS = 30;
  const SLOW_TICK_MS = 90;
  const DECEL_ZONE = 3;
  const STAGGER_MS = 80;

  class SplitFlap extends HTMLElement {
    static get observedAttributes() { return ["text", "count", "mode"]; }

    constructor() {
      super();
      this._cells = [];        // { el, charEl, drumIdx, timer }
      this._staggerTimers = [];
      this._clockTimer = null;
    }

    connectedCallback() {
      this._build();
      this._apply(this._targetText());
      this._startClock();
    }

    disconnectedCallback() {
      this._staggerTimers.forEach(clearTimeout);
      this._cells.forEach((c) => clearTimeout(c.timer));
      if (this._clockTimer) clearInterval(this._clockTimer);
    }

    attributeChangedCallback(name, oldValue, newValue) {
      if (oldValue === newValue || !this.isConnected) return;
      if (name === "count") this._build();
      if (name === "mode") this._startClock();
      this._apply(this._targetText());
    }

    get _count() {
      const explicit = parseInt(this.getAttribute("count") || "", 10);
      if (!isNaN(explicit) && explicit > 0) return explicit;
      return Math.max(1, (this.getAttribute("text") || "").length);
    }

    _targetText() {
      const mode = this.getAttribute("mode");
      const now = new Date();
      const p2 = (n) => String(n).padStart(2, "0");
      if (mode === "utc-time")
        return p2(now.getUTCHours()) + ":" + p2(now.getUTCMinutes()) + "Z";
      if (mode === "utc-date") {
        const months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN",
          "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
        return p2(now.getUTCDate()) + months[now.getUTCMonth()];
      }
      return this.getAttribute("text") || "";
    }

    _startClock() {
      if (this._clockTimer) clearInterval(this._clockTimer);
      this._clockTimer = null;
      const mode = this.getAttribute("mode");
      if (mode === "utc-time" || mode === "utc-date") {
        this._clockTimer = setInterval(() => this._apply(this._targetText()), 5000);
      }
    }

    _build() {
      this._staggerTimers.forEach(clearTimeout);
      this._cells.forEach((c) => clearTimeout(c.timer));
      this._cells = [];
      this.textContent = "";
      const count = this._count;
      for (let i = 0; i < count; i++) {
        const cell = document.createElement("span");
        cell.className = "sf-cell";
        const seam = document.createElement("span");
        seam.className = "sf-seam";
        const ch = document.createElement("span");
        ch.className = "sf-char";
        ch.textContent = "-";
        cell.appendChild(seam);
        cell.appendChild(ch);
        this.appendChild(cell);
        this._cells.push({ el: cell, charEl: ch, drumIdx: 0, timer: null });
      }
    }

    _apply(text) {
      const count = this._count;
      const padded = (text || "").toUpperCase().padEnd(count, " ").slice(0, count);
      this._staggerTimers.forEach(clearTimeout);
      this._staggerTimers = [];
      for (let i = 0; i < count; i++) {
        const target = padded.charAt(i);
        if (i === 0) {
          this._spin(this._cells[i], target);
        } else {
          this._staggerTimers.push(setTimeout(
            () => this._spin(this._cells[i], target), i * STAGGER_MS));
        }
      }
    }

    _spin(cell, targetChar) {
      if (!cell) return;
      clearTimeout(cell.timer);
      const targetIdx = DRUM.indexOf(targetChar);

      // Unknown character — snap directly with no animation.
      if (targetIdx < 0) {
        cell.charEl.textContent = targetChar;
        return;
      }

      // Solari animation switched off (webUi.solariAnimation) — snap without the drum spin.
      if (this.getAttribute("animate") === "off") {
        cell.drumIdx = targetIdx;
        this._setChar(cell, targetChar, false);
        return;
      }
      if (DRUM.charAt(cell.drumIdx) === targetChar) {
        this._setChar(cell, targetChar, false);
        return;
      }

      const forward = (targetIdx - cell.drumIdx + DRUM.length) % DRUM.length;
      let remaining = forward < MIN_FLIPS ? forward + DRUM.length : forward;

      const step = () => {
        cell.drumIdx = (cell.drumIdx + 1) % DRUM.length;
        this._setChar(cell, DRUM.charAt(cell.drumIdx), true);
        remaining--;
        if (remaining <= 0) { cell.timer = null; return; }
        let nextMs = FAST_TICK_MS;
        if (remaining <= DECEL_ZONE) {
          const t = 1 - remaining / DECEL_ZONE;
          nextMs = FAST_TICK_MS + t * (SLOW_TICK_MS - FAST_TICK_MS);
        }
        cell.timer = setTimeout(step, nextMs);
      };
      cell.timer = setTimeout(step, FAST_TICK_MS);
    }

    _setChar(cell, ch, animate) {
      cell.charEl.textContent = ch;
      if (animate) {
        // Restart the squeeze keyframe on every drum step.
        cell.charEl.classList.remove("tick");
        void cell.charEl.offsetWidth;
        cell.charEl.classList.add("tick");
      }
    }
  }

  if (!customElements.get("split-flap")) {
    customElements.define("split-flap", SplitFlap);
  }

  // ------------------------------------------------------------------- tab bar

  function updateChevrons(wrap) {
    const bar = wrap.querySelector(".tabbar");
    const left = wrap.querySelector(".chevron-left");
    const right = wrap.querySelector(".chevron-right");
    if (!bar || !left || !right) return;
    const overflow = bar.scrollWidth > bar.clientWidth + 1;
    left.style.display = overflow ? "" : "none";
    right.style.display = overflow ? "" : "none";
    left.disabled = bar.scrollLeft <= 0;
    right.disabled = bar.scrollLeft + bar.clientWidth >= bar.scrollWidth - 1;
  }

  function initTabBar(wrap) {
    const bar = wrap.querySelector(".tabbar");
    if (!bar || wrap._pcInit) return;
    wrap._pcInit = true;

    wrap.addEventListener("click", (e) => {
      const chevron = e.target.closest(".chevron");
      if (!chevron) return;
      const dir = chevron.classList.contains("chevron-left") ? -1 : 1;
      bar.scrollBy({ left: dir * Math.max(120, bar.clientWidth * 0.6), behavior: "smooth" });
    });

    bar.addEventListener("scroll", () => updateChevrons(wrap), { passive: true });
    new ResizeObserver(() => updateChevrons(wrap)).observe(bar);

    // Keep the active tab visible when Blazor swaps the `active` class on navigation.
    new MutationObserver(() => {
      const active = bar.querySelector(".tab.active");
      if (active) active.scrollIntoView({ block: "nearest", inline: "nearest" });
      updateChevrons(wrap);
    }).observe(bar, { subtree: true, attributes: true, attributeFilter: ["class"] });

    updateChevrons(wrap);
    const active = bar.querySelector(".tab.active");
    if (active) active.scrollIntoView({ block: "nearest", inline: "nearest" });
  }

  function scan() {
    document.querySelectorAll(".tabbar-wrap").forEach(initTabBar);
  }

  // The tab bar arrives when Blazor renders the interactive layout — watch for it.
  new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true });
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", scan);
  } else {
    scan();
  }

  // ------------------------------------------------------ circuit auto-recovery (#27)
  // iOS Safari freezes the page and kills the circuit's WebSocket when the tab backgrounds
  // or the iPad locks. blazor.web.js surfaces the outcome as classes on the reconnect
  // overlay:
  //   components-reconnect-failed   — retries exhausted (server may be back by now)
  //   components-reconnect-rejected — server reached but the circuit is gone
  // Left alone, both states wait forever behind a manual "reload" link, so a returning
  // remote client just sees a stale page. Recover automatically instead: keep retrying the
  // failed state (immediately on wake, then on a timer) and hard-reload the rejected state —
  // every store is a server-side singleton, so a reload fully restores the view.

  const RECONNECT_RETRY_MS = 4000;

  function initCircuitRecovery() {
    const modal = document.getElementById("components-reconnect-modal");
    if (!modal) return;

    let retryTimer = null;

    function state() {
      if (modal.classList.contains("components-reconnect-rejected")) return "rejected";
      if (modal.classList.contains("components-reconnect-failed")) return "failed";
      return "other";
    }

    async function attempt() {
      retryTimer = null;
      const s = state();
      if (s === "rejected") { location.reload(); return; }
      if (s !== "failed") return;
      try {
        // Resolves false when the server answered but refused the circuit — reload is the
        // only way forward. Success flips the overlay classes and recovery goes dormant.
        const reconnected = await Blazor.reconnect();
        if (reconnected === false) location.reload();
      } catch {
        schedule(RECONNECT_RETRY_MS); // Server unreachable — keep trying.
      }
    }

    function schedule(delayMs) {
      if (retryTimer === null) retryTimer = setTimeout(attempt, delayMs);
    }

    new MutationObserver(() => {
      const s = state();
      if (s === "rejected") location.reload();
      else if (s === "failed") schedule(RECONNECT_RETRY_MS);
    }).observe(modal, { attributes: true, attributeFilter: ["class"] });

    // The instant Safari thaws the page (or the network returns), try immediately rather
    // than waiting out the timer.
    const onWake = () => { if (state() !== "other") attempt(); };
    window.addEventListener("pageshow", onWake);
    window.addEventListener("online", onWake);
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible") onWake();
    });
  }

  initCircuitRecovery();
  initWakeLock();

  // ------------------------------------------------------ server-restart watchdog (#97)
  // A server restart kills every circuit, but when the WebSocket dies SILENTLY (the app was
  // restarted while this tab sat idle) blazor.web.js may never notice: the page keeps
  // rendering and ignores every click with no overlay at all — indistinguishable from "the
  // page is broken" (2026-08-22: six app restarts, a seemingly dead performance page). The
  // recovery above only helps once Blazor notices, so this watchdog asks the server
  // directly: poll its boot id and hard-reload the moment a different process answers.

  const BOOT_POLL_MS = 8000;

  function initServerRestartWatch() {
    let knownBootId = null;
    let inflight = false;

    async function check() {
      if (inflight || document.visibilityState !== "visible") return;
      inflight = true;
      try {
        const res = await fetch("/api/app/boot", { cache: "no-store" });
        if (!res.ok) return;
        const body = await res.json();
        if (!body || !body.bootId) return;
        if (knownBootId === null) { knownBootId = body.bootId; return; }
        if (body.bootId !== knownBootId) location.reload();
      } catch {
        // Server unreachable (mid-restart) — the next successful poll does the compare.
      } finally {
        inflight = false;
      }
    }

    check();
    setInterval(check, BOOT_POLL_MS);
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible") check();
    });
  }

  initServerRestartWatch();
})();
