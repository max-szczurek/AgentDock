(function () {
  var host = document.getElementById('preview-host');
  var form = document.getElementById('preview-form');
  var status = document.getElementById('preview-status');
  var agentId = host.dataset.agentId;
  fetch(form.action, { method: 'POST', body: new FormData(form), credentials: 'same-origin' })
    .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
    .then(function (data) {
      var frame = document.createElement('iframe');
      frame.title = 'Preview chat';
      frame.src = '/embed/' + agentId + '?preview=true#pt=' + encodeURIComponent(data.token);
      host.replaceChildren(frame);
    })
    .catch(function () { status.textContent = 'Could not start a preview session. Reload the page to try again.'; });
})();
