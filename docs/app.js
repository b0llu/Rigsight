// Rigsight site. No framework, no build step.
(() => {
  const REPO = "b0llu/Rigsight";
  const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;

  // ── Latest release: every Download button points straight at the newest installer ──
  // Buttons start as links to the Releases page, so they still work if this request fails.
  const CACHE_KEY = "rigsight-latest-release";

  async function latestRelease() {
    try {
      const cached = JSON.parse(sessionStorage.getItem(CACHE_KEY) || "null");
      if (cached && Date.now() - cached.at < 10 * 60 * 1000) return cached.data;
    } catch { /* storage unavailable */ }

    const res = await fetch(`https://api.github.com/repos/${REPO}/releases/latest`, { headers: { Accept: "application/vnd.github+json" } });
    if (!res.ok) throw new Error(`GitHub API ${res.status}`);
    const r = await res.json();
    const exe = (r.assets || []).find(a => /\.exe$/i.test(a.name));
    const data = {
      tag: r.tag_name,
      version: (r.tag_name || "").replace(/^v/i, ""),
      url: exe ? exe.browser_download_url : r.html_url,
      size: exe ? exe.size : 0,
      page: r.html_url,
      date: r.published_at,
      headline: firstHeadline(r.body || ""),
    };
    try { sessionStorage.setItem(CACHE_KEY, JSON.stringify({ at: Date.now(), data })); } catch { }
    return data;
  }

  // The first "### Something" heading of the release notes, without emoji: "Black and white themes".
  function firstHeadline(body) {
    const m = body.match(/^###\s+(.+)$/m);
    if (!m) return "";
    return m[1].replace(/[\p{Extended_Pictographic}‍️]/gu, "").replace(/\*\*/g, "").trim();
  }

  function ago(iso) {
    const days = Math.floor((Date.now() - new Date(iso)) / 86400000);
    if (days <= 0) return "today";
    if (days === 1) return "yesterday";
    if (days < 30) return `${days} days ago`;
    return new Date(iso).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
  }

  latestRelease().then(r => {
    document.querySelectorAll("[data-download]").forEach(a => {
      a.href = r.url;
      a.setAttribute("download", "");
    });
    const mb = r.size ? ` · ${Math.round(r.size / 1048576)} MB` : "";
    document.querySelectorAll("[data-version]").forEach(el => {
      el.textContent = `Version ${r.version}${mb}`;
      el.title = `Released ${ago(r.date)}`;
    });
    const pill = document.getElementById("release-pill");
    const text = document.getElementById("release-pill-text");
    pill.href = r.page;
    text.textContent = r.headline ? `New in ${r.version}: ${r.headline}` : `Version ${r.version} is out`;
  }).catch(() => { /* keep the Releases-page links */ });

  // ── Nav background once you scroll ──
  const nav = document.getElementById("nav");
  const onScroll = () => nav.classList.toggle("scrolled", scrollY > 8);
  addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  // ── Reveal on scroll ──
  const io = new IntersectionObserver(entries => {
    for (const e of entries) {
      if (!e.isIntersecting) continue;
      e.target.classList.add("in");
      e.target.querySelectorAll(".meters").forEach(m => m.classList.add("in"));
      if (e.target.classList.contains("meters")) e.target.classList.add("in");
      io.unobserve(e.target);
    }
  }, { threshold: 0.12, rootMargin: "0px 0px -40px 0px" });
  // The hero is on screen from the start: show it straight away rather than waiting for a scroll.
  document.querySelectorAll(".hero .reveal").forEach(el => requestAnimationFrame(() => el.classList.add("in")));
  document.querySelectorAll(".reveal:not(.hero .reveal)").forEach(el => io.observe(el));

  // ── Hero window: tilted at first, flattens as you scroll ──
  const stage = document.getElementById("stage");
  const win = stage && stage.querySelector(".window");
  if (win && !reduceMotion) {
    let ticking = false;
    const update = () => {
      ticking = false;
      const top = stage.getBoundingClientRect().top;
      const t = Math.min(1, Math.max(0, 1 - (top - innerHeight * 0.15) / (innerHeight * 0.6)));
      win.style.setProperty("--tilt", `${(14 * (1 - t)).toFixed(2)}deg`);
      win.style.setProperty("--sc", (0.96 + 0.04 * t).toFixed(4));
    };
    addEventListener("scroll", () => { if (!ticking) { ticking = true; requestAnimationFrame(update); } }, { passive: true });
    update();
  }

  // ── The mock overlay's readings drift like live ones ──
  const ranges = {
    fps: [138, 158, 0], low: [109, 126, 0], cpu: [58, 67, 1], gpu: [68, 76, 1],
    cpul: [31, 46, 0], gpul: [94, 99, 0], cpuw: [80, 96, 0], gpuw: [298, 331, 0], pump: [2110, 2170, 0],
  };
  const state = {};
  for (const [k, [lo, hi]] of Object.entries(ranges)) state[k] = (lo + hi) / 2;
  function tick() {
    for (const [k, [lo, hi, isTemp]] of Object.entries(ranges)) {
      const span = hi - lo;
      state[k] = Math.min(hi, Math.max(lo, state[k] + (Math.random() - 0.5) * span * 0.35));
      const v = Math.round(state[k]);
      document.querySelectorAll(`[data-tick="${k}"]`).forEach(el => {
        el.textContent = isTemp ? `${v}°` : String(v);
        if (isTemp) el.classList.toggle("hot", v >= (k === "gpu" ? 74 : 65));
      });
    }
  }
  if (!reduceMotion) setInterval(tick, 1000);

  // ── Dark / light comparison slider ──
  const cmp = document.getElementById("compare");
  if (cmp) {
    const input = cmp.querySelector("input");
    const set = v => cmp.style.setProperty("--pos", `${v}%`);
    input.addEventListener("input", () => set(input.value));
    // A gentle nudge the first time it scrolls into view, so it's obvious it moves.
    if (!reduceMotion) {
      const nudge = new IntersectionObserver(([e]) => {
        if (!e.isIntersecting) return;
        nudge.disconnect();
        const start = performance.now();
        const frame = now => {
          const p = Math.min(1, (now - start) / 1600);
          const v = 50 + Math.sin(p * Math.PI * 2) * 18 * (1 - p);
          set(v); input.value = v;
          if (p < 1) requestAnimationFrame(frame);
        };
        setTimeout(() => requestAnimationFrame(frame), 500);
      }, { threshold: 0.5 });
      nudge.observe(cmp);
    }
  }

  // ── Click a screenshot to see it full size ──
  const lb = document.getElementById("lightbox");
  const lbImg = lb.querySelector("img");
  document.querySelectorAll("[data-zoom] img").forEach(img => {
    img.addEventListener("click", () => {
      lbImg.src = img.currentSrc || img.src;
      lbImg.alt = img.alt;
      lb.hidden = false;
      document.body.style.overflow = "hidden";
    });
  });
  const closeLb = () => { lb.hidden = true; document.body.style.overflow = ""; };
  lb.addEventListener("click", closeLb);
  addEventListener("keydown", e => { if (e.key === "Escape" && !lb.hidden) closeLb(); });

  // ── Copy the PowerShell install command ──
  document.querySelectorAll("[data-copy]").forEach(btn => {
    btn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(btn.dataset.copy);
        btn.classList.add("done");
        setTimeout(() => btn.classList.remove("done"), 1800);
        showToast("Copied. Paste it into PowerShell.");
      } catch { /* clipboard blocked */ }
    });
  });

  // ── Phone visitors: copy the link to open on their PC ──
  const toast = document.getElementById("toast");
  function showToast(msg) {
    toast.textContent = msg;
    toast.classList.add("show");
    clearTimeout(showToast.t);
    showToast.t = setTimeout(() => toast.classList.remove("show"), 2200);
  }
  document.getElementById("copy-link").addEventListener("click", async () => {
    const url = location.href.split("#")[0];
    try {
      if (navigator.share) { await navigator.share({ title: "Rigsight", text: "Rigsight — know your rig", url }); return; }
      await navigator.clipboard.writeText(url);
      showToast("Link copied. Open it on your PC.");
    } catch { /* share sheet dismissed */ }
  });
})();
