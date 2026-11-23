const hostName = "com.winwindow.browser";
const session = crypto.randomUUID();
const grantedSites = new Set();
let port;
let timer;
let measuring = false;
let probing = false;
let changedWhileBusy = false;

function site(url) {
  try {
    const parsed = new URL(url);
    return /^https?:$/.test(parsed.protocol) ? `${parsed.protocol}//${parsed.hostname}/*` : null;
  } catch {
    return null;
  }
}

async function urlKey(url) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(url));
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
}

// This runs in the isolated world of an allowed top-level document. It returns only
// layout geometry and states, including for text inputs. No text or values are read.
function measurePage() {
  const width = innerWidth;
  const height = innerHeight;
  if (document.visibilityState !== "visible" || width < 80 || height < 60) return null;

  const visible = element => {
    const box = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    if (style.visibility !== "visible" || style.display === "none" || style.opacity === "0") return null;
    const left = Math.max(0, box.left);
    const top = Math.max(0, box.top);
    const right = Math.min(width, box.right);
    const bottom = Math.min(height, box.bottom);
    if (right <= left || bottom <= top) return null;
    return { x: left / width, y: top / height, width: (right - left) / width, height: (bottom - top) / height };
  };
  const area = rect => rect.width * rect.height;
  const largest = (elements, minimum) => {
    const regions = elements.map(element => ({ element, rect: visible(element) }));
    return regions.filter(({ rect }) => rect && area(rect) >= minimum)
      .sort((first, second) => area(second.rect) - area(first.rect))[0];
  };
  const readable = largest(Array.from(document.querySelectorAll("article")).slice(0, 16), 0.12) ??
    largest(Array.from(document.querySelectorAll("main, [role=main]")).slice(0, 8), 0.12);
  const videos = Array.from(document.querySelectorAll("video")).slice(0, 24)
    .filter(video => video.readyState >= 2 && video.videoWidth > 0 && video.videoHeight > 0);
  const isPlaying = video => !video.paused && !video.ended && video.playbackRate !== 0;
  const player = largest(videos.filter(isPlaying), 0.12) ?? largest(videos, 0.12);
  const active = document.activeElement;
  const editing = document.hasFocus() && !!active && (active.isContentEditable ||
    active.matches("textarea:enabled:not([readonly]), input:is([type=text],[type=search],[type=email],[type=url],[type=tel],:not([type])):enabled:not([readonly])"));
  return {
    viewportAspect: width / height,
    reading: readable?.rect ?? null,
    editing,
    video: player?.rect ?? null,
    playing: player ? isPlaying(player.element) : false,
  };
}

async function sample(window, channel) {
  const tab = window.tabs.find(candidate => candidate.active);
  const pattern = site(tab?.url);
  if (tab?.id == null || !pattern || tab.incognito || window.state === "minimized") return;
  const sampledAt = Date.now();
  let result;
  let documentId;
  if (!tab.discarded && await chrome.permissions.contains({ origins: [pattern] })) {
    try {
      const [frame] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: measurePage });
      result = frame?.result;
      documentId = frame?.documentId;
    } catch {
      // Navigation or a restricted browser page can revoke script access between queries.
    }
  }
  const [currentTab, currentWindow, key] = await Promise.all([
    chrome.tabs.get(tab.id), chrome.windows.get(window.id), urlKey(tab.url),
  ]);
  if (port !== channel || !currentTab.active || currentTab.url !== tab.url || currentTab.windowId !== window.id ||
      currentWindow.state !== window.state || currentWindow.left !== window.left || currentWindow.top !== window.top ||
      currentWindow.width !== window.width || currentWindow.height !== window.height ||
      Date.now() - sampledAt > 900) return;
  channel.postMessage({
    version: 1, sampledAt, session, tabId: tab.id, windowId: window.id, urlKey: key, documentId,
    left: currentWindow.left, top: currentWindow.top, width: currentWindow.width, height: currentWindow.height,
    focused: currentWindow.focused, audible: !!currentTab.audible && !currentTab.mutedInfo?.muted,
    hasContent: !!result, ...result,
  });
}

async function publish() {
  const channel = port;
  if (!channel || measuring) return;
  measuring = true;
  try {
    const windows = await chrome.windows.getAll({ populate: true, windowTypes: ["normal"] });
    const candidates = windows.filter(window => !window.incognito && window.tabs && window.state !== "minimized")
      .sort((first, second) => Number(second.focused) - Number(first.focused));
    await Promise.allSettled(candidates.slice(0, 6).map(window => sample(window, channel)));
    const focused = candidates[0]?.focused && candidates[0].tabs.find(tab => tab.active);
    const pattern = site(focused?.url);
    if (focused?.id != null) {
      const allowed = pattern && grantedSites.has(pattern);
      await chrome.action.setBadgeText({ tabId: focused.id, text: allowed ? "ON" : pattern ? "+" : "" });
      await chrome.action.setTitle({ tabId: focused.id, title: allowed
        ? "WinWindow: turn off page access for this site" : "WinWindow: allow page layout signals on this site" });
    }
  } finally {
    measuring = false;
  }
}

function probe(changed) {
  if (!port) return;
  if (probing || measuring) {
    changedWhileBusy ||= changed;
    return;
  }
  probing = true;
  port.postMessage(changed ? "changed" : "poll");
}

function refresh() {
  probe(true);
}

function connect() {
  if (port) return;
  const channel = chrome.runtime.connectNative(hostName);
  port = channel;
  channel.onMessage.addListener(async answer => {
    if (port !== channel) return;
    try {
      if (answer === "sample") await publish();
    } catch {
      // A window may close between enumeration and measurement.
    } finally {
      if (port === channel) {
        probing = false;
        if (changedWhileBusy) {
          changedWhileBusy = false;
          refresh();
        }
      }
    }
  });
  channel.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    port = undefined;
    probing = false;
    changedWhileBusy = false;
    clearInterval(timer);
    setTimeout(connect, 2500);
  });
  // Idle probes never enumerate tabs or run content scripts without a live desktop reply.
  timer = setInterval(() => probe(false), 1000);
  refresh();
}

chrome.action.onClicked.addListener(tab => {
  const pattern = site(tab.url);
  if (!pattern || tab.incognito) return;
  // Request in the toolbar gesture, before the first await.
  if (grantedSites.has(pattern)) chrome.permissions.remove({ origins: [pattern] }).then(refresh, refresh);
  else chrome.permissions.request({ origins: [pattern] }).then(refresh, refresh);
});
chrome.permissions.onAdded.addListener(permissions => {
  for (const origin of permissions.origins ?? []) grantedSites.add(origin);
  refresh();
});
chrome.permissions.onRemoved.addListener(permissions => {
  for (const origin of permissions.origins ?? []) grantedSites.delete(origin);
  refresh();
});
void chrome.permissions.getAll().then(permissions => {
  for (const origin of permissions.origins ?? []) grantedSites.add(origin);
  refresh();
});
chrome.tabs.onActivated.addListener(refresh);
chrome.tabs.onUpdated.addListener((_, change) => {
  if (change.url || change.status === "complete" || change.audible != null || change.mutedInfo) refresh();
});
chrome.windows.onFocusChanged.addListener(refresh);
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();
