/* The blog list (2026-11-18): the first rows come with the page, the rest load from list.json as you scroll, the
   chips filter by series, and the search box finds posts by their words, typos and half-typed words included,
   from search.json (title, summary and the whole text), loaded the first time someone searches. */
(function () {
  var list = document.getElementById('rows');
  if (!list) return;
  var PAGE = 10, more = document.getElementById('more'), box = document.getElementById('blog-search'),
      status = document.getElementById('search-status'), chips = document.querySelectorAll('.chip[data-series]');
  var all = null, docs = null, entries = null, shown = list.children.length, series = 'all', query = '', timer = 0;

  function getList() { return all ? Promise.resolve(all) : fetch('list.json').then(function (r) { return r.json(); }).then(function (d) { return (all = d); }); }
  function getDocs() {
    if (docs) return Promise.resolve(docs);
    return Promise.all([getList(), fetch('search.json').then(function (r) { return r.json(); })]).then(function (res) {
      docs = res[1].map(function (d, i) {
        return { i: i, text: d.b, title: set(d.t), desc: set(d.d + ' ' + d.l), body: set(d.b), full: norm(d.t + ' ' + d.d + ' ' + d.b) };
      });
      return docs;
    });
  }
  function norm(s) { return s.toLowerCase().normalize('NFKD').replace(/[̀-ͯ]/g, ''); }
  function words(s) { return norm(s).match(/[a-z0-9]+/g) || []; }
  function set(s) { var o = new Set(words(s)); return o; }
  function esc(s) { return s.replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  // How far apart two words are (letters added, removed, changed or swapped), stopping past max.
  function dist(a, b, max) {
    if (Math.abs(a.length - b.length) > max) return max + 1;
    var prev2 = null, prev = [], cur, i, j;
    for (j = 0; j <= b.length; j++) prev[j] = j;
    for (i = 1; i <= a.length; i++) {
      cur = [i]; var low = i;
      for (j = 1; j <= b.length; j++) {
        var c = a[i - 1] === b[j - 1] ? 0 : 1;
        cur[j] = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + c);
        if (prev2 && i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) cur[j] = Math.min(cur[j], prev2[j - 2] + 1);
        if (cur[j] < low) low = cur[j];
      }
      if (low > max) return max + 1;
      prev2 = prev; prev = cur;
    }
    return prev[b.length];
  }
  // The best a word does in one part of a post: the word itself, a word it starts, or one a typo or two away.
  function inField(term, field, hits) {
    if (field.has(term)) { hits.push(term); return 1; }
    var best = 0, max = term.length >= 8 ? 2 : 1;
    field.forEach(function (w) {
      if (best < 0.7 && term.length >= 2 && w.indexOf(term) === 0) { best = 0.7; hits.push(w); }
      else if (best < 0.5 && term.length >= 4 && dist(term, w, max) <= max) { best = 0.5; hits.push(w); }
    });
    return best;
  }
  var SMALL = new Set(['a', 'an', 'the', 'is', 'it', 'my', 'i', 'to', 'of', 'in', 'on', 'and', 'or', 'for', 'do', 'does', 'how', 'what', 'why',
    'much', 'can', 'with', 'while', 'when', 'your', 'you', 'are', 'be', 'at', 'this', 'that']);
  function search(q) {
    var terms = words(q), phrase = norm(q).trim(), out = [];
    var big = terms.filter(function (t) { return !SMALL.has(t); });
    if (big.length) terms = big;
    if (!terms.length) return out;
    docs.forEach(function (d) {
      var score = 0, matched = 0, hits = [];
      terms.forEach(function (t) {
        var s = 10 * inField(t, d.title, hits) + 4 * inField(t, d.desc, hits) + inField(t, d.body, hits);
        if (s > 0) matched++;
        score += s;
      });
      if (!matched) return;
      if (phrase.length > 3 && d.full.indexOf(phrase) >= 0) score += 15;
      out.push({ d: d, score: score, all: matched === terms.length, hits: hits });
    });
    var strict = out.filter(function (r) { return r.all; });
    return (strict.length ? strict : out).sort(function (a, b) { return b.score - a.score; });
  }
  function snippet(r) {
    var text = r.d.text, low = norm(text), at = -1, word = '';
    r.hits.forEach(function (h) { var k = low.search(new RegExp('\\b' + h.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))); if (k >= 0 && (at < 0 || k < at)) { at = k; word = h; } });
    if (at < 0) return '';
    var from = Math.max(0, text.lastIndexOf(' ', Math.max(0, at - 70)) + 1), to = Math.min(text.length, at + 130);
    var part = esc(text.slice(from, to));
    var marks = Array.from(new Set(r.hits)).sort(function (a, b) { return b.length - a.length; }).map(function (h) { return h.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); });
    part = part.replace(new RegExp('\\b(' + marks.join('|') + ')\\w*', 'gi'), '<mark>$&</mark>');
    return (from > 0 ? '… ' : '') + part + (to < text.length ? ' …' : '');
  }

  function render(from) {
    var html = '';
    for (var k = from; k < Math.min(entries.length, from + PAGE); k++) html += entries[k];
    list.insertAdjacentHTML('beforeend', html);
    shown = Math.min(entries.length, from + PAGE);
    more.hidden = shown >= entries.length;
  }
  function reset(newEntries) {
    entries = newEntries; list.innerHTML = ''; shown = 0; render(0);
  }
  function bySeries() { return all.filter(function (r) { return series === 'all' || r.s === series; }).map(function (r) { return r.h; }); }
  function showSeries() {
    getList().then(function () { reset(bySeries()); status.textContent = ''; });
  }
  function runSearch() {
    query = box.value.trim();
    if (!query) { showSeries(); return; }
    status.textContent = 'Searching…';
    getDocs().then(function () {
      var found = search(query).filter(function (r) { return series === 'all' || all[r.d.i].s === series; });
      reset(found.map(function (r) {
        var s = snippet(r);
        return all[r.d.i].h.replace('<p class="meta">', (s ? '<p class="hit">' + s + '</p>' : '') + '<p class="meta">');
      }));
      status.textContent = found.length ? (found.length === 1 ? '1 post matches.' : found.length + ' posts match.') : 'No posts match. Try fewer or shorter words.';
    });
  }

  chips.forEach(function (c) {
    c.addEventListener('click', function () {
      series = c.dataset.series;
      chips.forEach(function (x) { x.setAttribute('aria-pressed', x === c ? 'true' : 'false'); });
      if (query) runSearch(); else showSeries();
    });
  });
  if (box) {
    box.addEventListener('input', function () { clearTimeout(timer); timer = setTimeout(runSearch, 160); });
    box.addEventListener('focus', function () { getDocs(); }, { once: true });
  }
  if (more && 'IntersectionObserver' in window) {
    new IntersectionObserver(function (es) {
      if (!es[0].isIntersecting) return;
      if (entries) { if (shown < entries.length) render(shown); return; }
      getList().then(function () { entries = bySeries(); render(shown); });
    }, { rootMargin: '600px 0px' }).observe(more);
  } else if (more) {
    more.querySelector('button') && more.querySelector('button').addEventListener('click', function () {
      getList().then(function () { if (!entries) entries = bySeries(); render(shown); });
    });
  }
})();
