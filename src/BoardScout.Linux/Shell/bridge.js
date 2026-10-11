// Stands in for WebView2's window.chrome.webview inside the shared views (Connections, System, Topology).
// The dashboard shell keeps the one event stream to the server and hands each view its messages; a view's own
// requests go straight to the server.
(function () {
  'use strict';
  const view = document.currentScript.dataset.view;
  const listeners = [];
  const waiting = [];

  function deliver(data) {
    if (!listeners.length) { waiting.push(data); return; }
    for (const fn of listeners.slice()) {
      try { fn({ data }); } catch (err) { console.error(err); }
    }
  }

  function client() {
    try { return window.parent.BoardScoutShell ? window.parent.BoardScoutShell.client : ''; } catch { return ''; }
  }

  function post(message) {
    fetch('/api/msg/' + view, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-BoardScout': '1', 'X-BoardScout-Client': client() },
      body: JSON.stringify(message),
      credentials: 'same-origin'
    }).catch(() => {});
  }

  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: post,
    addEventListener(type, fn) {
      if (type !== 'message') return;
      listeners.push(fn);
      while (waiting.length && listeners.length) deliver(waiting.shift());
    },
    removeEventListener(type, fn) {
      const i = listeners.indexOf(fn);
      if (i >= 0) listeners.splice(i, 1);
    }
  };

  window.addEventListener('message', e => {
    if (e.origin !== location.origin || e.source !== window.parent) return;
    if (e.data && e.data.boardscout === view) deliver(e.data.msg);
  });

  // Ask for this view's settings and latest data once its own script has run.
  const ready = () => post({ type: 'ready' });
  if (document.readyState === 'complete') ready();
  else window.addEventListener('load', ready);
})();
