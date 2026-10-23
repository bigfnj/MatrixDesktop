export function formatClock(date, twelveHour) {
  const hours = date.getHours(), minutes = String(date.getMinutes()).padStart(2, "0");
  const seconds = String(date.getSeconds()).padStart(2, "0");
  return twelveHour ? `${hours % 12 || 12}:${minutes}:${seconds} ${hours < 12 ? "AM" : "PM"}`
    : `${String(hours).padStart(2, "0")}:${minutes}:${seconds}`;
}

export function createOverlays(config, warnings = []) {
  const layer = document.createElement("div");
  layer.className = "uc3d-overlays";
  Object.assign(layer.style, { position: "fixed", inset: "0", pointerEvents: "none", color: "#baffbc", textShadow: "0 0 9px #0a5", fontFamily: "monospace", zIndex: "2" });
  const clock = document.createElement("div"), caption = document.createElement("div"), status = document.createElement("div");
  clock.dataset.uc3dClock = ""; caption.dataset.uc3dCaption = "";
  Object.assign(clock.style, { position: "absolute", top: "50%", left: "50%", transform: "translate(-50%,-50%)", fontSize: `${Math.max(0.2, Number(config.clockSize) || 1) * 6}vmin`, whiteSpace: "nowrap" });
  Object.assign(caption.style, { position: "absolute", bottom: "6%", width: "90%", left: "5%", padding: "0.2em", boxSizing: "border-box", fontSize: "3vmin", overflowWrap: "anywhere", background: "rgba(0,0,0,.55)" });
  clock.hidden = !config.showClock;
  status.setAttribute("role", "status"); status.dataset.uc3dStatus = "";
  status.textContent = [...new Set(warnings)].join(" "); status.hidden = !status.textContent;
  Object.assign(status.style, { position: "absolute", top: "12px", left: "12px", maxWidth: "75%", fontSize: "14px", padding: "0.4em", background: "rgba(0,0,0,.75)", color: "#ddd", textShadow: "none", textAlign: "left" });
  layer.append(clock, caption, status); document.body.append(layer);
  let lastSecond = -1;
  return {
    addWarning(message) { warnings.push(message); status.textContent = [...new Set(warnings)].join(" "); status.hidden = false; },
    update(state, picture, date = new Date()) {
      if (config.showClock && Math.floor(date.getTime() / 1000) !== lastSecond) {
        clock.textContent = formatClock(date, config.clock12Hour); lastSecond = Math.floor(date.getTime() / 1000);
      }
      caption.textContent = config.pictureCaptions && picture && state.pictureWeight > 0 ? picture.caption ?? "" : "";
      caption.hidden = !caption.textContent;
      caption.style.opacity = String(state.pictureWeight);
    },
    dispose() { layer.remove(); },
  };
}
