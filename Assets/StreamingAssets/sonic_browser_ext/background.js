'use strict';

(function () {
  let nativePort = null;
  let activeTabId = null;
  let reconnectTimer = null;

  function ensureNativePort() {
    if (nativePort) return nativePort;
    try {
      const port = browser.runtime.connectNative('sonic_browser');
      port.onMessage.addListener((msg) => {
        if (!msg || typeof msg !== 'object') return;
        dispatchToPage(msg);
      });
      port.onDisconnect.addListener(() => {
        nativePort = null;
        if (!reconnectTimer) {
          reconnectTimer = setTimeout(() => {
            reconnectTimer = null;
            ensureNativePort();
          }, 500);
        }
      });
      nativePort = port;
    } catch (e) {
      nativePort = null;
    }
    return nativePort;
  }

  function dispatchToPage(msg) {
    if (activeTabId !== null) {
      browser.tabs.sendMessage(activeTabId, msg).catch(() => {
        broadcastToTabs(msg);
      });
      return;
    }
    broadcastToTabs(msg);
  }

  function broadcastToTabs(msg) {
    browser.tabs.query({}).then((tabs) => {
      if (!tabs || tabs.length === 0) return;
      for (const tab of tabs) {
        if (tab && typeof tab.id === 'number') {
          browser.tabs.sendMessage(tab.id, msg).catch(() => {});
        }
      }
    }).catch(() => {});
  }

  browser.runtime.onMessage.addListener((msg, sender) => {
    if (sender && sender.tab && typeof sender.tab.id === 'number') {
      activeTabId = sender.tab.id;
    }
    if (!msg || typeof msg !== 'object') return;
    const port = ensureNativePort();
    if (port) {
      try {
        port.postMessage(msg);
      } catch (e) {
        nativePort = null;
      }
    }
  });

  ensureNativePort();
})();
