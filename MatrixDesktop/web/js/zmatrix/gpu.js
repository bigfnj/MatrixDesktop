export function createProgram(gl, vertex, fragment) {
  const shaders = [];
  let program = null, linked = false;
  try {
    program = gl.createProgram();
    if (!program) throw new Error("ZMatrix could not allocate a WebGL program");
    for (const [type, source] of [[gl.VERTEX_SHADER, vertex], [gl.FRAGMENT_SHADER, fragment]]) {
      const shader = gl.createShader(type);
      if (!shader) throw new Error("ZMatrix could not allocate a WebGL shader");
      shaders.push(shader); gl.shaderSource(shader, source); gl.compileShader(shader);
      if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(`ZMatrix shader: ${gl.getShaderInfoLog(shader)}`);
      gl.attachShader(program, shader);
    }
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(`ZMatrix program: ${gl.getProgramInfoLog(program)}`);
    linked = true; return program;
  } finally {
    for (const shader of shaders) gl.deleteShader(shader);
    if (!linked && program) gl.deleteProgram(program);
  }
}
