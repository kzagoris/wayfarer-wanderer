// The Standard: OS-aware download, theme switcher over the real renders, and the Ctrl+B jump demo.
function detectOs() {
  const p = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || navigator.userAgent || "";
  if (/win/i.test(p)) return "windows";
  if (/mac|iphone|ipad/i.test(p)) return "macos";
  return "linux";
}
const reduced = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;

// Browsers without scroll-driven animations get the same reveals from an IntersectionObserver.
(function scrollRevealFallback() {
  const html = document.documentElement;
  if (reduced() || html.classList.contains("still") || (window.CSS && CSS.supports("animation-timeline: view()"))) return;
  if (!("IntersectionObserver" in window)) return;
  html.classList.add("io");
  const start = () => {
    const io = new IntersectionObserver((entries) => entries.forEach((e) => {
      if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); }
    }), { rootMargin: "0px 0px -12% 0px", threshold: 0.12 });
    document.querySelectorAll(".rise, .stack, .tilt, .draw-scroll, .open-band").forEach((el) => io.observe(el));
  };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();

document.addEventListener("alpine:init", () => {
  window.Alpine.data("standard", (downloads) => ({
    downloads,
    os: detectOs(),
    theme: "wayfarer",
    mode: "dark",
    get download() { return this.downloads.find((d) => d.id === this.os) || this.downloads[0]; },
    init() {
      // Warm the other renders so switching is instant.
      ["wayfarer", "cubicle", "high-contrast"].forEach((t) => ["dark", "light"].forEach((m) => { const i = new Image(); i.src = this.$root.querySelector(".window img").src.replace(/[^/]+\.webp$/, `${t}-${m}.webp`); }));
    },
    async copy(text) {
      try { await navigator.clipboard.writeText(text.replace(/^\$ /gm, "").trim()); } catch (_) { /* selectable fallback */ }
    },
  }));

  window.Alpine.data("jumpDemo", () => ({
    file: "java",
    hot: false,
    manual: false,
    pick(next) {
      this.manual = true;
      this.file = next;
      this.hot = true;
    },
    async loop() {
      if (reduced() || document.documentElement.classList.contains("still")) { this.hot = true; return; }
      const wait = (ms) => new Promise((r) => setTimeout(r, ms));
      const seen = new Promise((resolve) => {
        const io = new IntersectionObserver((e) => { if (e[0].isIntersecting) { io.disconnect(); resolve(); } }, { threshold: 0.6 });
        io.observe(this.$el);
      });
      await seen;
      while (!this.manual) {
        this.file = "java"; this.hot = false; await wait(1400);
        if (this.manual) break;
        this.hot = true; await wait(1100);
        if (this.manual) break;
        this.file = "kt"; await wait(2400);
      }
    },
  }));
});
