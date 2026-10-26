// ECAM-style diagnostics pages (ADR-0016): the drawing is a fixed 720×640 stage scaled as one
// piece to the width of its frame (the frame keeps a 9:8 aspect ratio in CSS), so nothing ever
// reflows and the layout is the same on a phone and an ultrawide. A ResizeObserver on the frame
// writes the scale factor into a CSS custom property the stage's transform reads.
(function () {
  "use strict";

  window.prosimCompanion = window.prosimCompanion || {};
  window.prosimCompanion.ecam = {
    attach: function (id, width) {
      const stage = document.getElementById(id);
      if (!stage || !stage.parentElement) return;
      const frame = stage.parentElement;
      const fit = () => {
        const k = frame.clientWidth / width;
        if (k > 0 && isFinite(k)) stage.style.setProperty("--ecam-scale", String(k));
      };
      if (stage._ecamObserver) stage._ecamObserver.disconnect();
      const observer = new ResizeObserver(fit);
      observer.observe(frame);
      stage._ecamObserver = observer;
      fit();
    },
    detach: function (id) {
      const stage = document.getElementById(id);
      if (stage && stage._ecamObserver) {
        stage._ecamObserver.disconnect();
        stage._ecamObserver = null;
      }
    }
  };
})();
