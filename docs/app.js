// RigSight site. No framework, no build step (built from the design artifact by tools\site-refresh\build.py).
(function () {
  var reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  // Counting what gets clicked (Umami, loaded after this file; nothing happens if it's blocked).
  var count = function (name) { try { window.umami && window.umami.track && window.umami.track(name); } catch (e) {} };
  var where = function (el) { return el.closest('.nav') ? 'top bar' : el.closest('.hero') ? 'first screen' : el.closest('.install') ? 'install steps' : el.closest('.footer') ? 'footer' : 'end'; };
  document.querySelectorAll('[data-download]').forEach(function (a) { a.addEventListener('click', function () { count('Download (' + where(a) + ')'); }); });
  document.querySelectorAll('a[href*="buymeacoffee.com"]').forEach(function (a) { a.addEventListener('click', function () { count('Buy me a coffee (' + where(a) + ')'); }); });
  document.querySelectorAll('[role="tab"]').forEach(function (t) { t.addEventListener('click', function () { count('Tab: ' + t.textContent.trim()); }); });
  document.querySelectorAll('#copy, [data-copy]').forEach(function (b) { b.addEventListener('click', function () { count('Copy the PowerShell command (' + where(b) + ')'); }); });
  document.querySelectorAll('#parts .pane-copy').forEach(function (c) { c.addEventListener('click', function () { if (window.matchMedia('(max-width: 640px)').matches) count('Phone: open ' + c.querySelector('.label').textContent.trim()); }); });


  // The peek: lies back under the opening screen, lies flat as it scrolls up.
  var peek = document.getElementById('peekwin'), peekT = false;
  function peekTilt() {
    peekT = false;
    var r = peek.getBoundingClientRect(), vh = window.innerHeight;
    var k = Math.min(1, Math.max(0, (vh - r.top) / (vh * 0.75)));
    peek.style.setProperty('--tilt', (16 * (1 - k)).toFixed(2) + 'deg');
  }
  if (!reduce) { requestAnimationFrame(peekTilt); window.addEventListener('scroll', function () { if (!peekT) { peekT = true; requestAnimationFrame(peekTilt); } }, { passive: true }); }

  // Top bar: hidden over the opening screen, shown once the page has scrolled a little.
  var nav = document.querySelector('.nav');
  function navCheck() { nav.classList.toggle('show', window.scrollY > window.innerHeight * 0.35); }
  navCheck(); window.addEventListener('scroll', navCheck, { passive: true });

  // Page background: streams in the part colours, all following one slowly changing flow so they move together in
  // bands. Each keeps its own tail, fading to nothing at the end, and the canvas is cleared every frame, so no ghost
  // paths build up and no bright head shows. Phones get fewer at about 30 frames a second, after load.
  (function () {
    var canvas = document.getElementById('flow');
    if (!canvas || reduce) return;
    var c = canvas.getContext('2d'), dpr = Math.min(window.devicePixelRatio || 1, 2), small = window.innerWidth < 700;
    var W, H, t = 0, ps = [], odd = false, cols = ['#5b8def', '#22d3ee', '#a78bfa', '#f472b6'], CH = 5;
    function h2(x, y) { var n = Math.sin(x * 12.9898 + y * 78.233) * 43758.5453; return n - Math.floor(n); }
    function noise(x, y) {
      var ix = Math.floor(x), iy = Math.floor(y), fx = x - ix, fy = y - iy;
      var a = h2(ix, iy), b = h2(ix + 1, iy), cc = h2(ix, iy + 1), d = h2(ix + 1, iy + 1);
      var ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
      return a + (b - a) * ux + (cc - a) * uy + (a - b - cc + d) * ux * uy;
    }
    function spawn(p) {
      p.x = Math.random() * W; p.y = Math.random() * H; p.tr = [];
      p.n = 30 + Math.floor(Math.random() * 45);       // tail length, in steps
      p.life = 0; p.max = 300 + Math.random() * 600;
      p.c = cols[Math.floor(Math.random() * cols.length)];
      // Three kinds: a faint, thin, slower layer behind (depth), the usual streaks, and about one in ten "leads",
      // thicker and brighter, so the part colours read at a glance.
      var r = Math.random();
      if (r < 0.35) { p.a = 0.12 + Math.random() * 0.08; p.w = 0.8; p.v = 0.55; }
      else if (r < 0.9) { p.a = 0.22 + Math.random() * 0.14; p.w = 1.2; p.v = 1; }
      else { p.a = 0.5 + Math.random() * 0.2; p.w = 1.9; p.v = 1.1; }
      return p;
    }
    function resize() {
      W = window.innerWidth; H = window.innerHeight;
      canvas.width = W * dpr; canvas.height = H * dpr; c.setTransform(dpr, 0, 0, dpr, 0, 0);
      var n = Math.round(Math.min(small ? 90 : 260, W * H / 5000));
      while (ps.length < n) { var q = spawn({}); q.life = Math.random() * q.max; ps.push(q); }
      ps.length = n;
    }
    function draw() {
      requestAnimationFrame(draw);
      if (document.hidden || (small && (odd = !odd))) return;
      c.clearRect(0, 0, W, H);
      c.lineCap = 'round'; c.lineJoin = 'round';
      // Full strength on the opening screen, easing to about half once the reader is past it, so text stays quiet.
      var dim = 1 - 0.5 * Math.min(1, window.scrollY / (H * 0.9));
      for (var i = 0; i < ps.length; i++) {
        var p = ps[i];
        var ang = noise(p.x * 0.004 + t * 0.0008, p.y * 0.004 + 100) * Math.PI * 6;
        var sp = 1 + noise(p.x * 0.003, p.y * 0.003 + 50) * 1.5;
        p.x += Math.cos(ang) * sp * p.v; p.y += Math.sin(ang) * sp * p.v; p.life++;
        if (p.x < -20 || p.x > W + 20 || p.y < -20 || p.y > H + 20 || p.life > p.max) { spawn(p); continue; }
        p.tr.push(p.x, p.y); if (p.tr.length > p.n * 2) p.tr.splice(0, 2);
        var pts = p.tr.length / 2; if (pts < 3) continue;
        var fade = Math.min(1, p.life / 80, (p.max - p.life) / 80);
        // The tail in a few pieces, faintest at the far end and strongest near the front.
        c.strokeStyle = p.c; c.lineWidth = p.w;
        for (var k = 0; k < CH; k++) {
          var from = Math.floor(pts * k / CH), to = Math.min(pts - 1, Math.floor(pts * (k + 1) / CH));
          if (to <= from) continue;
          c.globalAlpha = p.a * fade * dim * Math.pow((k + 1) / CH, 1.6);
          c.beginPath(); c.moveTo(p.tr[from * 2], p.tr[from * 2 + 1]);
          for (var j = from + 1; j <= to; j++) c.lineTo(p.tr[j * 2], p.tr[j * 2 + 1]);
          c.stroke();
        }
      }
      c.globalAlpha = 1; t++;
    }
    function start() { resize(); window.addEventListener('resize', resize); draw(); }
    if (!small || document.readyState === 'complete') start(); else window.addEventListener('load', start);
  })();

  // Pictures: start loading each one about a screen and a half before it scrolls into view, so it is ready when the
  // reader gets there; when a section with tabs comes near, every tab's picture loads, so switching never shows a gap.
  if ('IntersectionObserver' in window) {
    var near = new IntersectionObserver(function (es) {
      es.forEach(function (e) {
        if (!e.isIntersecting) return;
        var box = e.target.closest('.panes') || e.target;
        (box.tagName === 'IMG' ? [box] : box.querySelectorAll('img[loading="lazy"]')).forEach(function (im) { im.loading = 'eager'; near.unobserve(im); });
      });
    }, { rootMargin: '150% 0px 150% 0px' });
    document.querySelectorAll('img[loading="lazy"]').forEach(function (im) { near.observe(im); });
  }

  // Questions: answers slide open and closed instead of jumping.
  document.querySelectorAll('.faq-list details').forEach(function (d) {
    var sum = d.querySelector('summary'), ans = d.querySelector('p'), busy = null;
    sum.addEventListener('click', function (e) {
      if (reduce || !ans.animate) return;
      e.preventDefault();
      if (busy) busy.cancel();
      if (!d.open) {
        d.open = true;
        var h = ans.offsetHeight;
        busy = ans.animate([{ height: '0px', opacity: 0, transform: 'translateY(-6px)' }, { height: h + 'px', opacity: 1, transform: 'none' }], { duration: 320, easing: 'cubic-bezier(.2,.7,.2,1)' });
        busy.onfinish = function () { busy = null; };
      } else {
        busy = ans.animate([{ height: ans.offsetHeight + 'px', opacity: 1 }, { height: '0px', opacity: 0 }], { duration: 240, easing: 'ease-in' });
        busy.onfinish = function () { d.open = false; busy = null; };
      }
    });
  });

  var phone = window.matchMedia('(max-width: 640px)');
  // Phones: each part opens and closes on a tap (its name and figure always show).
  document.querySelectorAll('#parts .pane-copy').forEach(function (c) {
    var pane = c.closest('.pane');
    c.addEventListener('click', function (e) {
      if (!phone.matches || e.target.closest('a')) return;
      pane.classList.toggle('open');
      c.setAttribute('aria-expanded', pane.classList.contains('open'));
    });
  });

  // Tabs: panes share one grid cell, so switching never moves the page.
  var repicks = [];
  phone.addEventListener('change', function () { repicks.forEach(function (f) { f(); }); });
  document.querySelectorAll('[role="tablist"]').forEach(function (list) {
    var tabs = Array.prototype.slice.call(list.querySelectorAll('[role="tab"]')), current = tabs[0];
    repicks.push(function () { pick(current); });
    function pick(t) {
      current = t;
      tabs.forEach(function (x) {
        var on = x === t, p = document.getElementById(x.getAttribute('aria-controls'));
        x.setAttribute('aria-selected', on); x.tabIndex = on ? 0 : -1;
        // On phones every pane shows, one under another, so none is hidden or inert there.
        var all = phone.matches;
        p.setAttribute('aria-hidden', !on && !all); p.inert = !on && !all;
      });
    }
    tabs.forEach(function (t, i) {
      t.addEventListener('click', function () { pick(t); });
      t.addEventListener('keydown', function (e) {
        var d = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
        if (!d) return;
        var n = tabs[(i + d + tabs.length) % tabs.length]; pick(n); n.focus();
      });
    });
    pick(tabs[0]);
  });

  // Copy the install command.
  var toast = document.getElementById('toast');
  function say(t) { toast.textContent = t; toast.classList.add('show'); clearTimeout(say.t); say.t = setTimeout(function () { toast.classList.remove('show'); }, 1800); }
  document.querySelectorAll('#copy, [data-copy]').forEach(function (b) {
    b.addEventListener('click', function () {
      var text = 'irm https://rigsight.app/install.ps1 | iex';
      function pick() { var r = document.createRange(); r.selectNodeContents(b.parentNode.querySelector('code')); var s = getSelection(); s.removeAllRanges(); s.addRange(r); say('Selected. Press Ctrl+C to copy'); }
      try { navigator.clipboard.writeText(text).then(function () { say('Copied'); }, pick); } catch (e) { pick(); }
    });
  });
  // Ask RigSight's example questions: each shows its own answer in the chat beside them.
  document.querySelectorAll('[data-ask]').forEach(function (b) {
    b.addEventListener('click', function () {
      document.querySelectorAll('[data-ask]').forEach(function (o) {
        var on = o === b;
        o.setAttribute('aria-pressed', on ? 'true' : 'false');
        var p = document.getElementById('ask-' + o.dataset.ask);
        if (p) p.hidden = !on;
      });
      count('Ask example: ' + b.dataset.ask);
    });
  });
  // The plans' Monthly / Yearly switch: Yearly when the page opens, as in the app.
  document.querySelectorAll('[data-bill] button[data-bill]').forEach(function (b) {
    b.addEventListener('click', function () {
      var plans = document.getElementById('plans');
      plans.setAttribute('data-bill', b.dataset.bill);
      plans.querySelectorAll('button[data-bill]').forEach(function (o) { o.setAttribute('aria-pressed', o === b ? 'true' : 'false'); });
    });
  });
})();
