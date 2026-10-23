import { createRuntime, loadImage, loadText, startLoop, glyphSource, glyphConfig } from "../uc3d/runtime.js";

export function createProgram(gl, vertex, fragment) {
  const shaders = [];
  let result = null, linked = false;
  const compile = (type, code) => {
    const shader = gl.createShader(type);
    if (!shader) throw new Error("UC3D could not allocate a shader");
    shaders.push(shader);
    gl.shaderSource(shader, code); gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(`UC3D shader: ${gl.getShaderInfoLog(shader)}`);
    return shader;
  };
  try {
    result = gl.createProgram();
    if (!result) throw new Error("UC3D could not allocate a program");
    const vs = compile(gl.VERTEX_SHADER, vertex), fs = compile(gl.FRAGMENT_SHADER, fragment);
    gl.attachShader(result, vs); gl.attachShader(result, fs); gl.linkProgram(result);
    if (!gl.getProgramParameter(result, gl.LINK_STATUS)) throw new Error(`UC3D program: ${gl.getProgramInfoLog(result)}`);
    linked = true;
    return result;
  } finally {
    for (const shader of shaders) gl.deleteShader(shader);
    if (!linked && result) gl.deleteProgram(result);
  }
}

// A separate WebGL scene keeps the legacy REGL intensity-channel pipeline intact.
// It uses the same WebGL API as that backend; no native donor renderer is loaded.
export default async function startUC3D(canvas, config) {
  const gl = canvas.getContext("webgl", { alpha: false, antialias: false });
  if (!gl) throw new Error("UC3D requires a WebGL context");
  gl.getExtension("OES_standard_derivatives");
  const programs = [], buffers = [], textures = [], framebuffers = [];
  let runtime = null, dead = false, stop = null;
  const cleanup = () => {
    if (dead) return; dead = true;
    canvas.removeEventListener("webglcontextlost", contextLost);
    runtime?.dispose();
    for (const item of programs) gl.deleteProgram(item);
    for (const item of buffers) gl.deleteBuffer(item);
    for (const item of textures) gl.deleteTexture(item);
    for (const item of framebuffers) gl.deleteFramebuffer(item);
  };
  const contextLost = (event) => { event.preventDefault(); runtime?.diagnostics.errors.push("WebGL context was lost"); stop?.(); };
  const program = (vertex, fragment) => {
    const result = createProgram(gl, vertex, fragment);
    programs.push(result); return result;
  };
  const texture = (image = null, flipped = false) => {
    const result = gl.createTexture(); textures.push(result); gl.bindTexture(gl.TEXTURE_2D, result);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, flipped);
    if (image) gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, image);
    else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(4));
    return result;
  };
  try {
    const source = glyphSource(config);
    const [vertex, fragment, composite, atlas] = await Promise.all([
      loadText("shaders/glsl/uc3d.vert.glsl"), loadText("shaders/glsl/uc3d.frag.glsl"),
      loadText("shaders/glsl/uc3dComposite.frag.glsl"), loadImage(source.url),
    ]);
    runtime = await createRuntime(canvas, glyphConfig(config, source, atlas), "regl");
    const native = runtime.config.uc3dControls !== "legacy";
    const instances = native ? gl.getExtension("ANGLE_instanced_arrays") : null;
    if (native && !instances) throw new Error("UC3D original controls require WebGL instancing");
    const rainProgram = program(vertex, fragment);
    const compositeProgram = program("attribute vec2 position; varying vec2 uv; void main(){uv=(position+1.0)*0.5; gl_Position=vec4(position,0.0,1.0);}", composite);
    const glyphAtlas = texture(atlas, true); atlas.src = "";
    const desktop = texture(runtime.desktop, true), output = texture();
    const framebuffer = gl.createFramebuffer(); framebuffers.push(framebuffer);
    const geometry = gl.createBuffer(); buffers.push(geometry);
    const corners = gl.createBuffer(); buffers.push(corners);
    gl.bindBuffer(gl.ARRAY_BUFFER, corners); gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1]), gl.STATIC_DRAW);
    const triangle = gl.createBuffer(); buffers.push(triangle);
    gl.bindBuffer(gl.ARRAY_BUFFER, triangle); gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    const locations = Object.fromEntries(["projection", "scene0", "scene1", "scene2", "scene3", "scene4", "scene5", "scene6", "codeColor", "cursorColor", "glyphAtlas", "glyphOptions"].map(name => [name, gl.getUniformLocation(rainProgram, name)]));
    const attributes = (native ? [["cell", 4, 0], ["photo", 4, 16], ["origin", 4, 32], ["glyphState", 4, 48]]
      : [["cell", 4, 0], ["corner", 2, 16], ["photo", 4, 24]])
      .map(([name, size, offset]) => ({ location: gl.getAttribLocation(rainProgram, name), size, offset }));
    const cornerLocation = gl.getAttribLocation(rainProgram, "corner");
    const screenPosition = gl.getAttribLocation(compositeProgram, "position");
    const compositeLocations = Object.fromEntries(["rain", "desktop", "composition", "background"].map(name => [name, gl.getUniformLocation(compositeProgram, name)]));
    let width = 0, height = 0, geometrySize = 0;
    canvas.addEventListener("webglcontextlost", contextLost);
    stop = startLoop(canvas, runtime, (frame, w, h) => {
      if (w !== width || h !== height) {
        width = w; height = h; canvas.width = w; canvas.height = h;
        gl.bindTexture(gl.TEXTURE_2D, output); gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer); gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, output, 0);
        if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) throw new Error("UC3D render target is incomplete");
      }
      gl.bindBuffer(gl.ARRAY_BUFFER, geometry);
      if (frame.changed) {
        if (native) {
          if (geometrySize !== frame.grid.capacityBytes) {
            geometrySize = frame.grid.capacityBytes; gl.bufferData(gl.ARRAY_BUFFER, geometrySize, gl.DYNAMIC_DRAW);
          }
          if (frame.grid.data.byteLength) gl.bufferSubData(gl.ARRAY_BUFFER, 0, frame.grid.data);
        } else gl.bufferData(gl.ARRAY_BUFFER, frame.grid.data, gl.DYNAMIC_DRAW);
      }
      gl.useProgram(rainProgram); gl.viewport(0, 0, w, h); gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
      gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
      gl.enable(gl.BLEND); gl.blendFunc(native ? gl.SRC_ALPHA : gl.ONE, native ? gl.ONE_MINUS_SRC_ALPHA : gl.ONE); gl.disable(gl.DEPTH_TEST);
      for (const attr of attributes) {
        gl.enableVertexAttribArray(attr.location); gl.vertexAttribPointer(attr.location, attr.size, gl.FLOAT, false, native ? 64 : 40, attr.offset);
        if (native) instances.vertexAttribDivisorANGLE(attr.location, 1);
      }
      if (native) {
        gl.bindBuffer(gl.ARRAY_BUFFER, corners); gl.enableVertexAttribArray(cornerLocation);
        gl.vertexAttribPointer(cornerLocation, 2, gl.FLOAT, false, 8, 0); instances.vertexAttribDivisorANGLE(cornerLocation, 0);
      }
      gl.uniformMatrix4fv(locations.projection, false, frame.uniforms.subarray(0, 16));
      for (let i = 0; i < 7; i++) gl.uniform4fv(locations[`scene${i}`], frame.uniforms.subarray(16 + i * 4, 20 + i * 4));
      gl.uniform4fv(locations.codeColor, frame.uniforms.subarray(44, 48)); gl.uniform4fv(locations.cursorColor, frame.uniforms.subarray(48, 52));
      gl.uniform4fv(locations.glyphOptions, frame.uniforms.subarray(52, 56));
      gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, glyphAtlas); gl.uniform1i(locations.glyphAtlas, 0);
      if (native) instances.drawArraysInstancedANGLE(gl.TRIANGLES, 0, frame.grid.count, frame.grid.instanceCount);
      else gl.drawArrays(gl.TRIANGLES, 0, frame.grid.count);
      for (const attr of attributes) {
        gl.disableVertexAttribArray(attr.location);
        if (native) instances.vertexAttribDivisorANGLE(attr.location, 0);
      }
      if (native) gl.disableVertexAttribArray(cornerLocation);
      gl.bindFramebuffer(gl.FRAMEBUFFER, null); gl.disable(gl.BLEND); gl.useProgram(compositeProgram);
      gl.bindBuffer(gl.ARRAY_BUFFER, triangle); gl.enableVertexAttribArray(screenPosition); gl.vertexAttribPointer(screenPosition, 2, gl.FLOAT, false, 0, 0);
      gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, output); gl.uniform1i(compositeLocations.rain, 0);
      gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, desktop); gl.uniform1i(compositeLocations.desktop, 1);
      const blur = Math.max(0, runtime.config.bloomSize ?? 0.4) * 8;
      gl.uniform4fv(compositeLocations.composition, [blur / w, blur / h, blur ? runtime.config.bloomStrength ?? 0.7 : 0, runtime.config.highPassThreshold ?? 0.1]);
      gl.uniform4fv(compositeLocations.background, [...runtime.background, runtime.desktop ? 0.72 : 0]);
      gl.drawArrays(gl.TRIANGLES, 0, 3); gl.disableVertexAttribArray(screenPosition);
      const error = gl.getError(); if (error !== gl.NO_ERROR) throw new Error(`UC3D WebGL error ${error}`);
    }, cleanup);
    return stop;
  } catch (error) { runtime?.diagnostics.errors.push(error.message); cleanup(); throw error; }
}
