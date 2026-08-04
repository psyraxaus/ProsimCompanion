// ProsimCompanion web UI client-side behaviours. Everything here is presentation-only and
// deliberately runs in the browser so nothing animates over the Blazor Server circuit:
//   1. <split-flap> custom element — the Solari display ported from the predecessor's WPF
//      SplitFlapCharacterControl (same drum, min-flip count, deceleration zone, per-tick
//      squeeze). Blazor only sets the `text` attribute; UTC clock modes self-update.
//   2. Tab bar overflow chevrons + keeping the active tab scrolled into view.

(function () {
  "use strict";

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
})();
