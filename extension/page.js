(() => {
  function report() {
    const videos = [...document.querySelectorAll("video")].filter(video => video.duration > 0);
    const video = videos.find(candidate => !candidate.paused) ?? videos[0];

    try {
      chrome.runtime.sendMessage({
        type: "page",
        url: location.href,
        title: document.title,
        video: video
          ? { position: video.currentTime, duration: Number.isFinite(video.duration) ? video.duration : null, paused: video.paused }
          : null
      }).catch(stopIfOrphaned);
    } catch {
      stopIfOrphaned();
    }
  }

  function answer(message, sender, reply) {
    if (message.type !== "fetch") return;
    const url = new URL(message.path, location.origin);

    if (url.origin !== location.origin) {
      reply(null);
      return;
    }

    fetch(url, { credentials: "include" })
      .then(response => (response.ok ? response.text() : null))
      .then(reply, () => reply(null));
    return true;
  }

  function stopIfOrphaned() {
    if (!chrome.runtime?.id) clearInterval(timer);
  }

  clearInterval(globalThis.auraTimer);

  try {
    if (globalThis.auraAnswer) chrome.runtime.onMessage.removeListener(globalThis.auraAnswer);
  } catch {
  }

  const timer = setInterval(report, 2000);
  globalThis.auraTimer = timer;
  globalThis.auraAnswer = answer;
  chrome.runtime.onMessage.addListener(answer);
  report();
})();
