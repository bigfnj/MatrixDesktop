// A scene disposes its own overlays and warnings when rendering fails, so the failure
// text needs an element of its own that outlives the scene. Errors are not warnings:
// suppressWarnings does not hide it.
export function showRenderFailure(scene, error) {
  const notice = document.createElement("div");
  notice.className = "render-failure"; notice.setAttribute("role", "alert");
  notice.textContent = `${scene} rendering failed: ${error?.message ?? String(error)}`;
  Object.assign(notice.style, { position: "fixed", left: "12px", bottom: "12px", maxWidth: "min(720px,90vw)", color: "#fff",
    background: "rgba(0,0,0,.85)", padding: "8px", font: "12px sans-serif", zIndex: "10", pointerEvents: "none" });
  document.body.append(notice);
  return notice;
}
