(function () {
  'use strict';
  var app = document.getElementById('app');
  var agentId = app.dataset.agentId;
  var preview = app.dataset.preview === '1';
  var inFrame = window.parent !== window;
  var API = '/api/widget/v1';
  var store = preview ? window.sessionStorage : window.localStorage;
  var K = {};
  function setKeys(sessionId) {
    var p = 'agentdock:v1:' + agentId + (sessionId ? ':s:' + sessionId : '');
    K = { token: p + ':token', conv: p + ':conv', model: 'agentdock:v1:' + agentId + ':model' };
  }
  setKeys(null);

  var state = { token: null, convId: null, config: null, hostOrigin: null, busy: false, ctrl: null, sessionId: null };
  var els = {};

  function get(k) { try { return store.getItem(k); } catch (e) { return null; } }
  function set(k, v) { try { if (v == null) store.removeItem(k); else store.setItem(k, v); } catch (e) { /* storage unavailable */ } }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }

  if (window.DOMPurify) {
    DOMPurify.addHook('afterSanitizeAttributes', function (node) {
      if (node.tagName === 'A') { node.setAttribute('target', '_blank'); node.setAttribute('rel', 'noopener noreferrer'); }
    });
  }
  var MARKER = /\u27E6(\d+)\u27E7/g;

  function safeUrl(u) { return /^https?:\/\//i.test(u) ? u : null; }

  // Citation markers are inserted by the server after cited text; turn each into a superscript link to its source.
  function applyCitations(root, sources) {
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    var nodes = [];
    while (walker.nextNode()) if (walker.currentNode.nodeValue.indexOf('\u27E6') >= 0) nodes.push(walker.currentNode);
    nodes.forEach(function (n) {
      var frag = document.createDocumentFragment(), text = n.nodeValue, last = 0, m;
      MARKER.lastIndex = 0;
      while ((m = MARKER.exec(text))) {
        frag.appendChild(document.createTextNode(text.slice(last, m.index)));
        last = m.index + m[0].length;
        var src = sources && sources[m[1]];
        var url = src && safeUrl(src.url);
        if (url) {
          var sup = el('sup', 'cite'), a = el('a', null, m[1]);
          a.href = url; a.target = '_blank'; a.rel = 'noopener noreferrer nofollow';
          if (src.citedText) a.title = src.citedText;
          sup.appendChild(a); frag.appendChild(sup);
        }
      }
      frag.appendChild(document.createTextNode(text.slice(last)));
      n.parentNode.replaceChild(frag, n);
    });
  }

  function renderMarkdown(target, text, sources) {
    var html = window.marked ? marked.parse(text, { gfm: true, breaks: true, async: false }) : null;
    if (html == null || !window.DOMPurify) { target.textContent = text.replace(MARKER, ''); return; }
    target.innerHTML = DOMPurify.sanitize(html, {
      FORBID_TAGS: ['img', 'style', 'form', 'input', 'button', 'textarea', 'select', 'iframe', 'object', 'embed', 'svg', 'math', 'link', 'meta'],
      FORBID_ATTR: ['style', 'srcset'],
      ALLOWED_URI_REGEXP: /^(?:https?:|mailto:)/i
    });
    applyCitations(target, sources);
  }

  function host(url) { try { return new URL(url).hostname.replace(/^www\./, ''); } catch (e) { return ''; } }

  function sourcesBlock(list) {
    var shown = list.filter(function (s) { return safeUrl(s.url); });
    if (!shown.length) return null;
    var d = el('details', 'sources'), sum = el('summary', null, 'Sources (' + shown.length + ')'), ol = el('ol');
    shown.forEach(function (s) {
      var li = el('li'), a = el('a', null, s.title || s.url);
      a.href = s.url; a.target = '_blank'; a.rel = 'noopener noreferrer nofollow';
      if (s.citedText) a.title = s.citedText;
      li.value = s.index; li.appendChild(a);
      var h = host(s.url); if (h) li.appendChild(el('span', 'dom', ' \u00b7 ' + h));
      ol.appendChild(li);
    });
    d.appendChild(sum); d.appendChild(ol);
    return d;
  }

  function toHost(msg) {
    if (!inFrame || !state.hostOrigin) return;
    msg.source = 'agentdock';
    window.parent.postMessage(msg, state.hostOrigin);
  }

  function authHeaders(extra) {
    var h = extra || {};
    if (state.token) h['Authorization'] = 'Bearer ' + state.token;
    return h;
  }

  function createSession() {
    return fetch(API + '/agents/' + agentId + '/sessions', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ hostOrigin: state.hostOrigin, sessionId: state.sessionId })
    }).then(function (r) {
      if (!r.ok) throw new Error('session ' + r.status);
      return r.json();
    }).then(function (d) { state.token = d.token; set(K.token, d.token); });
  }

  // Calls the API; on 401 in public mode it mints a fresh session once and retries.
  function api(path, opts, retried) {
    opts = opts || {};
    opts.headers = authHeaders(opts.headers);
    return fetch(API + path, opts).then(function (r) {
      if (r.status === 401 && !preview && !retried && state.hostOrigin) {
        state.token = null; set(K.token, null); set(K.conv, null); state.convId = null;
        return createSession().then(function () { return api(path, opts, true); });
      }
      return r;
    });
  }

  function addMsg(role, text, opts) {
    var d = el('div', 'm ' + role);
    var sources = opts && opts.sources;
    if (role === 'assistant') renderMarkdown(d, text || '', sources); else d.textContent = text;
    if (opts && opts.incomplete) d.classList.add('incomplete');
    els.msgs.appendChild(d);
    if (sources) {
      var list = Object.keys(sources).map(function (k) { return sources[k]; }).sort(function (a, b) { return a.index - b.index; });
      var block = sourcesBlock(list);
      if (block) els.msgs.appendChild(block);
    }
    scroll();
    return d;
  }
  function note(text, cls) { var d = el('div', cls || 'note', text); els.msgs.appendChild(d); scroll(); return d; }
  function scroll() { els.msgs.scrollTop = els.msgs.scrollHeight; }

  function buildUi(cfg) {
    var a = cfg.appearance;
    var root = document.documentElement.style;
    root.setProperty('--primary', a.primaryColor);
    root.setProperty('--on-primary', a.textOnPrimary);
    root.setProperty('--bg', a.panelBackground);
    document.title = a.title;

    var hd = el('div', 'hd');
    hd.appendChild(el('h1', null, a.title));
    if (cfg.modelSelector) {
      var sel = el('select');
      sel.setAttribute('aria-label', 'Model');
      cfg.modelSelector.families.forEach(function (f) {
        var o = el('option', null, f.charAt(0).toUpperCase() + f.slice(1)); o.value = f; sel.appendChild(o);
      });
      var saved = get(K.model);
      sel.value = cfg.modelSelector.families.indexOf(saved) >= 0 ? saved : cfg.modelSelector.defaultFamily;
      sel.addEventListener('change', function () { set(K.model, sel.value); });
      els.model = sel; hd.appendChild(sel);
    }
    var nb = el('button', null, 'New'); nb.title = 'New conversation'; nb.id = 'new-conv';
    nb.addEventListener('click', newConversation);
    hd.appendChild(nb);
    if (inFrame && !preview) {
      var cb = el('button', null, '\u00d7'); cb.title = 'Close'; cb.setAttribute('aria-label', 'Close');
      cb.addEventListener('click', function () { toHost({ type: 'agentdock:close' }); });
      hd.appendChild(cb);
    }

    els.msgs = el('div', 'msgs'); els.msgs.setAttribute('aria-live', 'polite');
    var ft = el('div', 'ft');
    els.input = el('textarea'); els.input.rows = 1; els.input.maxLength = cfg.maxMessageChars || 4000;
    els.input.placeholder = a.placeholder || 'Type a message\u2026';
    els.input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
    });
    els.input.addEventListener('input', function () { els.input.style.height = 'auto'; els.input.style.height = Math.min(els.input.scrollHeight, 120) + 'px'; });
    els.send = el('button', null, 'Send'); els.send.id = 'send';
    els.send.addEventListener('click', function () { state.busy ? stop() : send(); });
    ft.appendChild(els.input); ft.appendChild(els.send);

    app.replaceChildren(hd, els.msgs, ft);
  }

  function setBusy(b) {
    state.busy = b;
    els.send.textContent = b ? 'Stop' : 'Send';
    els.send.classList.toggle('stop', b);
    if (els.model) els.model.disabled = b;
    els.input.disabled = b;
    els.msgs.classList.toggle('busy', b);
    if (!b) els.input.focus();
  }

  function loadHistory() {
    els.msgs.replaceChildren();
    var welcome = state.config.appearance.welcomeMessage;
    var id = get(K.conv);
    var done = function () { if (welcome && !els.msgs.querySelector('.m')) addMsg('assistant', welcome); };
    var pick = id ? Promise.resolve(id) : (state.sessionId && !state.fresh ? api('/conversations').then(function (r) {
      return r.ok ? r.json() : [];
    }).then(function (list) { return list.length ? list[0].id : null; }) : Promise.resolve(null));
    return pick.then(function (cid) {
      id = cid;
      if (!id) { state.convId = null; done(); return null; }
      set(K.conv, id);
      return loadMessages(id, done);
    });
  }

  function loadMessages(id, done) {
    return api('/conversations/' + id + '/messages').then(function (r) {
      if (!r.ok) { set(K.conv, null); state.convId = null; return; }
      return r.json().then(function (d) {
        state.convId = id;
        d.messages.forEach(function (m) {
          var sources = null;
          (m.citations || []).forEach(function (c) { sources = sources || {}; sources[c.index] = c; });
          addMsg(m.role, m.text, { incomplete: m.incomplete, sources: sources });
        });
        (d.pendingApprovals || []).forEach(function (a) { approvalCard(a); });
      });
    }).then(done);
  }

  function newConversation() {
    if (state.busy) return;
    set(K.conv, null); state.convId = null; state.fresh = true;
    loadHistory().then(function () { state.fresh = false; });
  }

  function ensureConversation() {
    if (state.convId) return Promise.resolve(state.convId);
    return api('/conversations', { method: 'POST' }).then(function (r) {
      if (!r.ok) throw new Error('conversation ' + r.status);
      return r.json();
    }).then(function (d) { state.convId = d.id; set(K.conv, d.id); return d.id; });
  }

  function stop() {
    if (!state.busy) return;
    if (state.convId) api('/conversations/' + state.convId + '/cancel', { method: 'POST' }).catch(function () {});
    if (state.ctrl) state.ctrl.abort();
  }

  function approvalCard(a) {
    var card = el('div', 'approval');
    card.dataset.id = a.toolExecutionId;
    var title = el('div', 'approval-title');
    title.appendChild(document.createTextNode('The assistant wants to use '));
    title.appendChild(el('strong', null, a.name));
    card.appendChild(title);
    if (a.summary) card.appendChild(el('pre', 'approval-summary', a.summary));
    var row = el('div', 'approval-actions');
    var yes = el('button', 'approve', 'Approve'); yes.type = 'button';
    var no = el('button', 'deny', 'Deny'); no.type = 'button';
    yes.addEventListener('click', function () { decide(a.toolExecutionId, true, card); });
    no.addEventListener('click', function () { decide(a.toolExecutionId, false, card); });
    row.appendChild(yes); row.appendChild(no); card.appendChild(row);
    els.msgs.appendChild(card); scroll();
    return card;
  }

  function decide(id, approve, card) {
    if (state.busy || !state.convId) return;
    card.classList.add('decided');
    card.querySelector('.approval-actions').replaceChildren(el('span', 'muted', approve ? 'Approved' : 'Declined'));
    runStream(function () {
      state.ctrl = new AbortController();
      return api('/conversations/' + state.convId + '/approvals/' + id, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ approve: approve }), signal: state.ctrl.signal
      });
    }, function (d) {
      card.remove();
      note((d.error && d.error.message) || 'Something went wrong. Please try again.', 'err');
      loadHistory();
    });
  }

  function send() {
    var text = els.input.value.trim();
    if (!text || state.busy) return;
    els.input.value = ''; els.input.style.height = 'auto';
    var userEl = addMsg('user', text);
    runStream(function () {
      return ensureConversation().then(function (cid) {
        state.ctrl = new AbortController();
        var body = { text: text };
        if (els.model) body.model = els.model.value;
        return api('/conversations/' + cid + '/turns', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal: state.ctrl.signal
        });
      });
    }, function (d) {
      userEl.remove();
      els.input.value = text;
      note((d.error && d.error.message) || 'Something went wrong. Please try again.', 'err');
    });
  }

  // Runs one streamed request (a chat turn or an approval decision) and renders its events.
  function runStream(request, onReject) {
    setBusy(true);
    var botEl = null, raw = '', raf = 0, stopped = false, chip = null, toolChip = null, sources = {}, nSources = 0;

    function paint() { raf = 0; if (botEl) { renderMarkdown(botEl, raw, sources); scroll(); } }
    function finish(opts) {
      if (raf) { cancelAnimationFrame(raf); paint(); }
      if (nSources) {
        var block = sourcesBlock(Object.keys(sources).map(function (k) { return sources[k]; }).sort(function (a, b) { return a.index - b.index; }));
        if (block) { els.msgs.appendChild(block); scroll(); }
      }
      if (botEl) { botEl.classList.remove('streaming'); if (opts && opts.incomplete) botEl.classList.add('incomplete'); }
      setBusy(false);
    }

    request().then(function (r) {
      if (!r.ok) {
        return r.json().catch(function () { return {}; }).then(function (d) { onReject(d); finish(); });
      }
      if ((r.headers.get('content-type') || '').indexOf('text/event-stream') < 0) {
        return r.json().catch(function () { return {}; }).then(function () { finish(); loadHistory(); });
      }
      var reader = r.body.getReader(), dec = new TextDecoder(), buf = '';
      function handle(name, data) {
        if (name === 'text.delta') {
          if (!botEl) { botEl = addMsg('assistant', ''); botEl.classList.add('streaming'); }
          raw += data.text;
          if (!raf) raf = requestAnimationFrame(paint);
        } else if (name === 'citation') {
          sources[data.index] = data; nSources++;
        } else if (name === 'search.started') {
          chip = el('div', 'chip', 'Searching the web for \u201c' + data.query + '\u201d\u2026');
          els.msgs.appendChild(chip); scroll();
        } else if (name === 'search.completed') {
          if (chip) chip.textContent = 'Found ' + data.resultCount + ' result' + (data.resultCount === 1 ? '' : 's');
          chip = null;
        } else if (name === 'search.failed') {
          if (chip) chip.textContent = 'Search unavailable';
          chip = null;
        } else if (name === 'tool.started') {
          toolChip = el('div', 'chip', 'Using ' + data.name + '\u2026');
          els.msgs.appendChild(toolChip); scroll();
        } else if (name === 'tool.completed') {
          if (toolChip) toolChip.textContent = 'Used ' + data.name;
          toolChip = null;
        } else if (name === 'tool.failed') {
          if (toolChip) toolChip.textContent = data.name + ' failed: ' + (data.message || 'unavailable');
          toolChip = null;
        } else if (name === 'approval.required') {
          approvalCard(data);
        } else if (name === 'approval.decided') {
          note(data.status === 'Expired' ? 'That request expired.' : 'Request declined.');
        } else if (name === 'message.completed') {
          if (data.stopReason === 'cancelled') stopped = true;
          else if (data.stopReason !== 'awaiting_approval') toHost({ type: 'agentdock:unread' });
        } else if (name === 'error') {
          if (botEl) botEl.classList.add('incomplete');
          note(data.message || 'Something went wrong.', 'err');
          stopped = !!botEl;
        }
      }
      function pump() {
        return reader.read().then(function (chunk) {
          if (chunk.done) { finish({ incomplete: stopped }); return; }
          buf += dec.decode(chunk.value, { stream: true });
          var idx;
          while ((idx = buf.indexOf('\n\n')) >= 0) {
            var block = buf.slice(0, idx); buf = buf.slice(idx + 2);
            var name = 'message', data = '';
            block.split('\n').forEach(function (line) {
              if (line.indexOf('event:') === 0) name = line.slice(6).trim();
              else if (line.indexOf('data:') === 0) data += line.slice(5).trim();
            });
            if (data) { try { handle(name, JSON.parse(data)); } catch (e) { /* ignore malformed event */ } }
          }
          return pump();
        });
      }
      return pump();
    }).catch(function (e) {
      if (e && e.name === 'AbortError') { finish({ incomplete: !!botEl }); return; }
      note('Connection problem. Please try again.', 'err');
      finish({ incomplete: !!botEl });
    });
  }

  function start() {
    return api('/agents/' + agentId + '/config').then(function (r) {
      if (!r.ok) throw new Error('config ' + r.status);
      return r.json();
    }).then(function (cfg) {
      state.config = cfg;
      buildUi(cfg);
      return loadHistory();
    });
  }

  function fail(msg) { app.replaceChildren(el('div', 'note', msg)); }

  function boot() {
    if (preview) {
      var m = /[#&]pt=([^&]+)/.exec(location.hash);
      if (m) { set(K.token, decodeURIComponent(m[1])); history.replaceState(null, '', location.pathname + location.search); }
      state.token = get(K.token);
      if (!state.token) return fail('Preview session missing. Reload the preview page.');
      return start().catch(function () { fail('Preview unavailable.'); });
    }
    if (!inFrame) return fail('This chat must be embedded on an allowed website.');

    window.addEventListener('message', function onInit(ev) {
      if (ev.source !== window.parent || !ev.data || ev.data.type !== 'agentdock:init') return;
      window.removeEventListener('message', onInit);
      state.hostOrigin = ev.origin;
      var sid = typeof ev.data.sessionId === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(ev.data.sessionId) ? ev.data.sessionId.toLowerCase() : null;
      if (sid) { state.sessionId = sid; setKeys(sid); }
      state.token = get(K.token);
      var ready = state.token ? Promise.resolve() : createSession();
      ready.then(start).catch(function () { fail('This assistant is not available on this site.'); });
    });
    window.parent.postMessage({ source: 'agentdock', type: 'agentdock:ready' }, '*');
  }

  boot();
})();
