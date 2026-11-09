// gl.getError() waits for the GPU process in Chromium: read on every frame it cost the UC3D and
// ZMatrix passes 0.4 to 0.5 ms of renderer time a frame (AUD-52). A WebGL error stays recorded
// until it is read, so a later read still reports it. The returned check reads the error on the
// first warmupFrames frames (setup), on any frame passed force (one that allocates), and then once
// every everyFrames frames, about once a second when that is the frame rate. An error throws
// "<label> WebGL error <code>", so it still reaches diagnostics.errors and the render-failure notice.
export function createErrorCheck(gl, label, { warmupFrames = 3, everyFrames = 60 } = {}) {
  const requested = Math.round(Number(everyFrames));
  const every = Number.isFinite(requested) ? Math.max(1, Math.min(120, requested)) : 60;
  let frame = 0;
  return (force = false) => {
    frame++;
    if (!force && frame > warmupFrames && frame % every !== 0) return;
    const error = gl.getError();
    if (error !== gl.NO_ERROR) throw new Error(`${label} WebGL error ${error}`);
  };
}
