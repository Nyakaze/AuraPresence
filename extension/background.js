const address = "ws://localhost:47300/";

let socket = null;
let hosts = [];

function connect() {
  if (socket) return;
  socket = new WebSocket(address);
  socket.onopen = () => {
    send({ type: "hello", version: chrome.runtime.getManifest().version, browser: browserName() });
    indicate(true);
  };
  socket.onclose = () => {
    socket = null;
    hosts = [];
    indicate(false);
  };
  socket.onmessage = event => receive(JSON.parse(event.data));
}

function send(message) {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
}

async function receive(message) {
  if (message.type === "hosts") await watch(message.hosts);
  else if (message.type === "fetch") await fetchIn(message);
  else if (message.type === "reload") chrome.runtime.reload();
}

async function watch(list) {
  hosts = list;
  const matches = list.map(host => `*://${host}/*`);
  await chrome.scripting.unregisterContentScripts();
  if (matches.length === 0) return;

  try {
    await chrome.scripting.registerContentScripts([{ id: "page", js: ["page.js"], matches, runAt: "document_idle" }]);
  } catch (error) {
    console.warn("Aura sent hosts the browser can't match.", list, error);
    return;
  }

  for (const tab of await chrome.tabs.query({ url: matches }))
    chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ["page.js"] }).catch(() => {});
}

async function fetchIn({ id, tab, path }) {
  let body = null;
  try {
    body = await chrome.tabs.sendMessage(tab, { type: "fetch", path });
  } catch {
  }
  send({ type: "fetched", id, body: body ?? null });
}

function indicate(connected) {
  chrome.action.setBadgeText({ text: connected ? "on" : "off" });
  chrome.action.setBadgeBackgroundColor({ color: connected ? "#3BA55D" : "#80848E" });
  chrome.action.setTitle({ title: connected ? "Connected to Aura" : "Aura isn't connected. Is Aura running?" });
}

function browserName() {
  const brands = navigator.userAgentData?.brands ?? [];
  const brand = brands.find(entry => !/not.a.brand|chromium/i.test(entry.brand));
  return brand?.brand ?? "Chromium";
}

chrome.runtime.onMessage.addListener((message, sender) => {
  if (message.type !== "page" || !sender.tab) return;
  connect();
  if (!hosts.includes(new URL(message.url).hostname)) return;
  send({ ...message, tab: sender.tab.id, active: sender.tab.active });
});

chrome.tabs.onRemoved.addListener(tab => send({ type: "gone", tab }));

chrome.alarms.onAlarm.addListener(connect);
chrome.alarms.create("connect", { periodInMinutes: 0.5 });

indicate(false);
connect();
