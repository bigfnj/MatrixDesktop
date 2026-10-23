import { createRuntime, startLoop } from "../zmatrix/runtime.js";
import { createProgram } from "../zmatrix/gpu.js";

// The persistent 2D scene is shared; this pass really presents it through WebGL.
export default async function startZMatrix(canvas, config) {
  const gl = canvas.getContext("webgl", { alpha: false, antialias: false });
  if (!gl) throw new Error("ZMatrix requires a WebGL context");
  let runtime = null, stop = null, dead = false, program = null, texture = null, geometry = null, width = 0, height = 0;
  const cleanup = () => {
    if (dead) return; dead = true;
    window.removeEventListener("pagehide", cleanup); canvas.removeEventListener("webglcontextlost", contextLost);
    runtime?.dispose();
    if (program) gl.deleteProgram(program); if (texture) gl.deleteTexture(texture); if (geometry) gl.deleteBuffer(geometry);
    program = null; texture = null; geometry = null;
  };
  const contextLost = event => {
    event.preventDefault(); runtime?.diagnostics.errors.push("ZMatrix WebGL context was lost");
    if (stop) stop(); else cleanup();
  };
  window.addEventListener("pagehide", cleanup, { once: true }); canvas.addEventListener("webglcontextlost", contextLost);
  try {
    runtime = await createRuntime(canvas, config, "regl", Math.min(8192, gl.getParameter(gl.MAX_TEXTURE_SIZE)));
    if (dead) { runtime.dispose(); return cleanup; }
    program = createProgram(gl,
      "attribute vec2 position; varying vec2 uv; void main(){uv=(position+1.0)*0.5; gl_Position=vec4(position,0.0,1.0);}",
      "precision highp float; varying vec2 uv; uniform sampler2D image; void main(){gl_FragColor=vec4(texture2D(image,uv).rgb,1.0);}");
    texture = gl.createTexture(); geometry = gl.createBuffer();
    if (!texture || !geometry) throw new Error("ZMatrix could not allocate WebGL presentation resources");
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true); gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
    gl.bindBuffer(gl.ARRAY_BUFFER, geometry); gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    const position = gl.getAttribLocation(program, "position"), image = gl.getUniformLocation(program, "image");
    stop = startLoop(canvas, runtime, frame => {
      const resized = width !== frame.width || height !== frame.height;
      if (resized) { width = frame.width; height = frame.height; canvas.width = width; canvas.height = height; }
      gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, texture);
      if (resized) gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, frame.source);
      else if (frame.changed) gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, gl.RGBA, gl.UNSIGNED_BYTE, frame.source);
      gl.viewport(0, 0, width, height); gl.disable(gl.BLEND); gl.disable(gl.DEPTH_TEST); gl.useProgram(program);
      gl.bindBuffer(gl.ARRAY_BUFFER, geometry); gl.enableVertexAttribArray(position); gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
      gl.uniform1i(image, 0); gl.drawArrays(gl.TRIANGLES, 0, 3); gl.disableVertexAttribArray(position);
      const error = gl.getError(); if (error !== gl.NO_ERROR) throw new Error(`ZMatrix WebGL error ${error}`);
    }, cleanup);
    return stop;
  } catch (error) { runtime?.diagnostics.errors.push(error.message); cleanup(); throw error; }
}
