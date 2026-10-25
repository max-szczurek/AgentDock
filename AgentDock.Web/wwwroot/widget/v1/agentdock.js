(function () {
  'use strict';
  var script = document.currentScript;
  if (!script || !script.src) return;
  var agentId = script.getAttribute('data-agent-id');
  if (!agentId) return;
  var adOrigin = new URL(script.src).origin;
  var rawSessionId = script.getAttribute('data-session-id');
  var sessionId = rawSessionId && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(rawSessionId.trim()) ? rawSessionId.trim() : null;

  var ICONS = {
    chat: '<path d="M4 4h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H9l-5 4v-4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z"/>',
    sparkle: '<path d="M12 2l2.2 6.3L21 10l-6.8 1.7L12 18l-2.2-6.3L3 10l6.8-1.7z"/>',
    help: '<path d="M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm1 15h-2v-2h2zm1.5-6.2c-.7.6-1.5 1-1.5 2.2h-2c0-1.9 1.1-2.6 1.8-3.2.5-.4.7-.7.7-1.1 0-.7-.6-1.2-1.5-1.2s-1.5.5-1.5 1.3H8.5c0-1.9 1.5-3.3 3.5-3.3s3.5 1.2 3.5 3c0 1-.4 1.7-1 2.5z"/>'
  };

  fetch(adOrigin + '/api/widget/v1/agents/' + encodeURIComponent(agentId) + '/config', { credentials: 'omit' })
    .then(function (r) { if (!r.ok) throw new Error('unavailable'); return r.json(); })
    .then(mount)
    .catch(function () { /* agent disabled or unreachable: render nothing */ });

  function mount(cfg) {
    var a = cfg.appearance;
    var host = document.createElement('div');
    host.setAttribute('data-agentdock', agentId);
    var root = host.attachShadow({ mode: 'closed' });
    var side = a.position === 'bottom-left' ? 'left' : 'right';
    var style = document.createElement('style');
    style.textContent =
      ':host{all:initial}' +
      '.btn{position:fixed;' + side + ':20px;bottom:20px;width:56px;height:56px;border-radius:50%;border:0;cursor:pointer;z-index:2147483646;' +
      'background:' + a.primaryColor + ';color:' + a.textOnPrimary + ';box-shadow:0 4px 14px rgba(0,0,0,.25);display:flex;align-items:center;justify-content:center;padding:0}' +
      '.btn svg{width:26px;height:26px;fill:currentColor}' +
      '.dot{position:absolute;top:6px;right:6px;width:12px;height:12px;border-radius:50%;background:#ef4444;display:none}' +
      '.panel{position:fixed;' + side + ':20px;bottom:88px;width:' + a.panelWidth + 'px;height:' + a.panelHeight + 'px;max-width:calc(100vw - 24px);max-height:calc(100vh - 108px);' +
      'border:0;border-radius:14px;overflow:hidden;box-shadow:0 10px 40px rgba(0,0,0,.3);z-index:2147483647;background:#fff;display:none}' +
      '.panel.open{display:block}.panel iframe{width:100%;height:100%;border:0;display:block}' +
      '@media (max-width:480px){.panel{left:8px;right:8px;bottom:8px;width:auto;height:calc(100vh - 16px);max-height:none}}';
    var btn = document.createElement('button');
    btn.className = 'btn'; btn.type = 'button'; btn.setAttribute('aria-label', a.title); btn.setAttribute('aria-expanded', 'false');
    btn.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true">' + (ICONS[a.iconPreset] || ICONS.chat) + '</svg><span class="dot"></span>';
    var dot = btn.querySelector('.dot');
    var panel = document.createElement('div'); panel.className = 'panel';
    root.appendChild(style); root.appendChild(panel); root.appendChild(btn);
    document.body.appendChild(host);

    var frame = null;
    function open() {
      if (!frame) {
        frame = document.createElement('iframe');
        frame.title = a.title;
        frame.allow = 'clipboard-write';
        frame.src = adOrigin + '/embed/' + encodeURIComponent(agentId) + '?v=1';
        panel.appendChild(frame);
      }
      panel.classList.add('open'); btn.setAttribute('aria-expanded', 'true'); dot.style.display = 'none';
    }
    function close() { panel.classList.remove('open'); btn.setAttribute('aria-expanded', 'false'); }
    btn.addEventListener('click', function () { panel.classList.contains('open') ? close() : open(); });

    window.addEventListener('message', function (ev) {
      if (ev.origin !== adOrigin || !frame || ev.source !== frame.contentWindow) return;
      var d = ev.data;
      if (!d || d.source !== 'agentdock') return;
      if (d.type === 'agentdock:ready') frame.contentWindow.postMessage({ source: 'agentdock', type: 'agentdock:init', v: 1, sessionId: sessionId }, adOrigin);
      else if (d.type === 'agentdock:close') close();
      else if (d.type === 'agentdock:unread' && !panel.classList.contains('open')) dot.style.display = 'block';
    });
  }
})();
