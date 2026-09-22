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

  // Opens (or focuses) the pop-out Flight Monitor window. Same origin, so the onboarded
  // browser cookie carries over; a popup that the browser blocked falls back to a tab.
  window.prosimCompanion.openMonitor = function () {
    const url = "monitor";
    const win = window.open(url, "prosim-flight-monitor", "popup=yes,width=1600,height=900");
    if (win) { win.focus(); return true; }
    window.open(url, "_blank");
    return false;
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
