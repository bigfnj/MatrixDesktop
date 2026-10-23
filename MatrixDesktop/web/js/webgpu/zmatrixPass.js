import { createRuntime, startLoop } from "../zmatrix/runtime.js";

const shader = `
struct Vertex { @builtin(position) position: vec4f, @location(0) uv: vec2f };
@vertex fn vertexMain(@builtin(vertex_index) index: u32) -> Vertex {
  let positions = array<vec2f,3>(vec2f(-1.0,-1.0),vec2f(3.0,-1.0),vec2f(-1.0,3.0));
  let p = positions[index]; var result: Vertex;
  result.position = vec4f(p,0.0,1.0); result.uv = vec2f((p.x+1.0)*0.5,1.0-(p.y+1.0)*0.5); return result;
}
@group(0) @binding(0) var imageSampler: sampler;
@group(0) @binding(1) var image: texture_2d<f32>;
@fragment fn fragmentMain(input: Vertex) -> @location(0) vec4f { return vec4f(textureSample(image,imageSampler,input.uv).rgb,1.0); }
`;
export default async function startZMatrix(canvas, config) {
  let runtime = null, device = null, context = null, texture = null, stop = null, dead = false;
  const gpuError = event => {
    if (dead) return;
    runtime?.diagnostics.errors.push(event.error.message); console.error("ZMatrix WebGPU validation:", event.error.message);
    if (stop) stop(); else cleanup();
  };
  const cleanup = () => {
    if (dead) return; dead = true; window.removeEventListener("pagehide", cleanup);
    runtime?.dispose(); texture?.destroy(); texture = null;
    device?.removeEventListener("uncapturederror", gpuError);
    try { context?.unconfigure(); } finally { device?.destroy(); }
  };
  window.addEventListener("pagehide", cleanup, { once: true });
  try {
    const adapter = await navigator.gpu.requestAdapter();
    if (dead) return cleanup;
    if (!adapter) throw new Error("ZMatrix WebGPU: no adapter available");
    device = await adapter.requestDevice();
    if (dead) { device.destroy(); return cleanup; }
    device.addEventListener("uncapturederror", gpuError);
    device.lost.then(info => { if (!dead) { runtime?.diagnostics.errors.push(`WebGPU device lost: ${info.message}`); if (stop) stop(); else cleanup(); } });
    context = canvas.getContext("webgpu");
    if (!context) throw new Error("ZMatrix WebGPU context is unavailable");
    const format = navigator.gpu.getPreferredCanvasFormat(); context.configure({ device, format, alphaMode: "opaque" });
    runtime = await createRuntime(canvas, config, "webgpu", Math.min(8192, device.limits.maxTextureDimension2D));
    if (dead) { runtime.dispose(); return cleanup; }
    const module = device.createShaderModule({ code: shader });
    const messages = (await module.getCompilationInfo()).messages.filter(message => message.type === "error");
    if (messages.length) throw new Error(`ZMatrix WGSL: ${messages.map(message => `${message.lineNum}: ${message.message}`).join("; ")}`);
    const pipeline = await device.createRenderPipelineAsync({ layout: "auto", vertex: { module, entryPoint: "vertexMain" },
      fragment: { module, entryPoint: "fragmentMain", targets: [{ format }] }, primitive: { topology: "triangle-list" } });
    if (dead) return cleanup;
    const sampler = device.createSampler({ minFilter: "nearest", magFilter: "nearest" });
    let width = 0, height = 0, bindings = null;
    stop = startLoop(canvas, runtime, frame => {
      const resized = width !== frame.width || height !== frame.height;
      if (resized) {
        width = frame.width; height = frame.height; canvas.width = width; canvas.height = height;
        texture?.destroy(); texture = device.createTexture({ size: [width, height], format: "rgba8unorm",
          usage: GPUTextureUsage.COPY_DST | GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.RENDER_ATTACHMENT });
        bindings = device.createBindGroup({ layout: pipeline.getBindGroupLayout(0), entries: [
          { binding: 0, resource: sampler }, { binding: 1, resource: texture.createView() }] });
      }
      if (resized || frame.changed) device.queue.copyExternalImageToTexture({ source: frame.source }, { texture }, [width, height]);
      const encoder = device.createCommandEncoder();
      const pass = encoder.beginRenderPass({ colorAttachments: [{ view: context.getCurrentTexture().createView(),
        loadOp: "clear", storeOp: "store", clearValue: { r: 0, g: 0, b: 0, a: 1 } }] });
      pass.setPipeline(pipeline); pass.setBindGroup(0, bindings); pass.draw(3); pass.end(); device.queue.submit([encoder.finish()]);
    }, cleanup);
    return stop;
  } catch (error) { runtime?.diagnostics.errors.push(error.message); cleanup(); throw error; }
}
