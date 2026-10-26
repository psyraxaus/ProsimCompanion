// ECAM-style diagnostics pages (ADR-0016): the drawing is a fixed 720×640 stage scaled as one
// piece so it FITS THE WINDOW — whichever is tighter, the width of the content column or the
// height left under the app header (owner request 2026-10-27: no page scroll on a wide, short
// window). The frame is sized to the scaled stage so the layout around it stays honest, and a
// ResizeObserver on the page plus the window resize event re-fit on every change (sidebar
// collapsing, tablet rotation, browser zoom).
(function () {
  "use strict";

  window.prosimCompanion = window.prosimCompanion || {};
  window.prosimCompanion.ecam = {
    attach: function (id, width, height) {
      const stage = document.getElementById(id);
      if (!stage || !stage.parentElement || !stage.parentElement.parentElement) return;
      const frame = stage.parentElement;
      const host = frame.parentElement;
      const fit = () => {
        const style = window.getComputedStyle(host);
        const padX = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
        const padBottom = parseFloat(style.paddingBottom) || 0;
        const availW = host.clientWidth - padX;
        const frameTop = frame.getBoundingClientRect().top;
        // The scrolling pane is <main>; its bottom edge (above the footer) is the limit.
        const pane = host.closest("main");
        const bottom = pane ? pane.getBoundingClientRect().bottom : window.innerHeight;
        const availH = bottom - frameTop - padBottom - 4;
        let k = Math.min(availW / width, availH / height);
        if (!isFinite(k) || k <= 0) k = availW / width;
        k = Math.max(0.25, k);
        frame.style.width = (width * k) + "px";
        frame.style.height = (height * k) + "px";
        stage.style.setProperty("--ecam-scale", String(k));
      };
      if (stage._ecamDetach) stage._ecamDetach();
      const observer = new ResizeObserver(fit);
      observer.observe(host);
      window.addEventListener("resize", fit);
      stage._ecamDetach = () => {
        observer.disconnect();
        window.removeEventListener("resize", fit);
        stage._ecamDetach = null;
      };
      fit();
      // The header's split-flaps and fonts settle a moment after first paint; fit once more.
      setTimeout(fit, 150);
    },
    detach: function (id) {
      const stage = document.getElementById(id);
      if (stage && stage._ecamDetach) stage._ecamDetach();
    }
  };
})();
