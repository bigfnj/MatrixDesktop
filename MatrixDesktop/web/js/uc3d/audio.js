// Original synthesized sounds. No music or sound effects from the donor are bundled.
export function createAudio(config, bootstrap, canvas) {
  let context = null, element = null, disposed = false, started = false, button = null;
  const nodes = [];
  const volume = Math.max(0, Math.min(1, Number(config.soundVolume) || 0));
  const state = { mode: config.sound ?? "off", status: "off" };
  const start = async () => {
    if (disposed || state.mode === "off") return;
    try {
      const localUrl = state.mode === "music" ? bootstrap.audioUrl : bootstrap.effectsUrl;
      if (localUrl) {
        if (!element) { element = new Audio(localUrl); element.loop = true; element.volume = volume; }
        else if (state.status === "failed") element.load();
        await element.play();
      } else {
        if (!context) {
          const AudioContext = window.AudioContext || window.webkitAudioContext;
          if (!AudioContext) throw new Error("Web Audio is unavailable");
          context = new AudioContext();
          const master = context.createGain(); master.gain.value = volume * (state.mode === "effects" ? 0.3 : 0.12); master.connect(context.destination); nodes.push(master);
          if (state.mode === "effects") {
            const buffer = context.createBuffer(1, context.sampleRate * 3, context.sampleRate);
            const data = buffer.getChannelData(0);
            for (let i = 0; i < data.length; i++) data[i] = (Math.random() * 2 - 1) * (0.12 + Math.sin(i / context.sampleRate * 7) ** 12 * 0.8);
            const source = context.createBufferSource(); source.buffer = buffer; source.loop = true;
            const filter = context.createBiquadFilter(); filter.type = "bandpass"; filter.frequency.value = 1800; filter.Q.value = 0.7;
            source.connect(filter); filter.connect(master); source.start(); nodes.push(source, filter);
          } else {
            for (const frequency of [55, 82.4069, 110, 164.8138, 220]) {
              const oscillator = context.createOscillator(); oscillator.type = "sine"; oscillator.frequency.value = frequency;
              const gain = context.createGain(); gain.gain.value = 0.12;
              oscillator.connect(gain); gain.connect(master); oscillator.start(); nodes.push(oscillator, gain);
            }
          }
        }
        await context.resume();
        if (context.state !== "running") throw new Error("Sound needs a user gesture");
      }
      if (disposed) { element?.pause(); if (context && context.state !== "closed") context.close(); return; }
      started = true; state.status = "playing"; button?.remove(); button = null;
    } catch (error) {
      if (disposed) return;
      const gestureRequired = error.name === "NotAllowedError" || (context && context.state === "suspended" && error.message === "Sound needs a user gesture");
      state.status = gestureRequired ? "waiting-for-gesture" : "failed";
      if (!button) {
        button = document.createElement("button");
        Object.assign(button.style, { position: "fixed", right: "16px", bottom: "16px", zIndex: "3" });
        button.addEventListener("click", start); document.body.append(button);
      }
      button.textContent = gestureRequired ? "Enable sound" : "Retry sound";
      console.info(`UC3D sound: ${error.message}`);
    }
  };
  const visibility = () => {
    if (document.hidden) { context?.suspend(); element?.pause(); }
    else if (started) start();
  };
  if (state.mode !== "off") document.addEventListener("visibilitychange", visibility);
  if (state.mode !== "off") {
    if (bootstrap.preview) {
      state.status = "preview-muted";
      button = document.createElement("button"); button.textContent = "Test sound";
      Object.assign(button.style, { position: "fixed", right: "12px", bottom: "12px", zIndex: "3" });
      button.addEventListener("click", start); document.body.append(button);
    } else start();
  }
  return {
    state,
    dispose() {
      disposed = true; state.status = "disposed"; document.removeEventListener("visibilitychange", visibility);
      button?.remove(); if (element) { element.pause(); element.removeAttribute("src"); element.load(); }
      for (const node of nodes) { try { node.stop?.(); node.disconnect(); } catch {} }
      context?.close();
    },
  };
}
