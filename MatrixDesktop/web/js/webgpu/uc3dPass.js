import { createRuntime, loadImage, loadText, startLoop, glyphSource, glyphConfig } from "../uc3d/runtime.js";

export default async function startUC3D(canvas, config) {
  const adapter = await navigator.gpu.requestAdapter();
  if (!adapter) throw new Error("UC3D WebGPU: no adapter available");
  const device = await adapter.requestDevice();
  let runtime = null, stop = null, dead = false, geometry = null, target = null, context = null;
  const buffers = [], textures = [];
  const cleanup = () => {
    if (dead) return; dead = true;
    try {
      runtime?.dispose(); geometry?.destroy(); target?.destroy();
      for (const buffer of buffers) buffer.destroy(); for (const texture of textures) texture.destroy();
    } finally {
      try { context?.unconfigure(); }
      finally { device.destroy(); }
    }
  };
  const buffer = (size, usage) => { const item = device.createBuffer({ size, usage }); buffers.push(item); return item; };
  const imageTexture = (image, flipY = false) => {
    const item = device.createTexture({ size: image ? [image.naturalWidth, image.naturalHeight] : [1, 1], format: "rgba8unorm",
      usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT });
    textures.push(item);
    if (image) device.queue.copyExternalImageToTexture({ source: image, flipY }, { texture: item }, [image.naturalWidth, image.naturalHeight]);
    return item;
  };
  const bind = (pipeline, entries) => device.createBindGroup({ layout: pipeline.getBindGroupLayout(0), entries: entries.map((resource, binding) => ({ binding, resource })) });
  try {
    context = canvas.getContext("webgpu");
    if (!context) throw new Error("UC3D WebGPU: canvas context is unavailable");
    const format = navigator.gpu.getPreferredCanvasFormat();
    const source = glyphSource(config);
    const [shader, compositeShader, atlas] = await Promise.all([
      loadText("shaders/wgsl/uc3d.wgsl"), loadText("shaders/wgsl/uc3dComposite.wgsl"),
      loadImage(source.url),
    ]);
    runtime = await createRuntime(canvas, glyphConfig(config, source, atlas), "webgpu");
    const native = runtime.config.uc3dControls !== "legacy";
    context.configure({ device, format, alphaMode: "opaque" });
    const module = device.createShaderModule({ code: shader }), compositeModule = device.createShaderModule({ code: compositeShader });
    for (const item of [module, compositeModule]) {
      const info = await item.getCompilationInfo();
      const errors = info.messages.filter(message => message.type === "error");
      if (errors.length) throw new Error(`UC3D WGSL: ${errors.map(error => `${error.lineNum}: ${error.message}`).join("; ")}`);
    }
    const additive = { operation: "add", srcFactor: "one", dstFactor: "one" };
    const sourceAlpha = { operation: "add", srcFactor: "src-alpha", dstFactor: "one-minus-src-alpha" };
    const nativeLayout = [{ arrayStride: 64, stepMode: "instance", attributes: [
      { shaderLocation: 0, offset: 0, format: "float32x4" }, { shaderLocation: 2, offset: 16, format: "float32x4" },
      { shaderLocation: 3, offset: 32, format: "float32x4" }, { shaderLocation: 4, offset: 48, format: "float32x4" },
    ] }, { arrayStride: 8, attributes: [{ shaderLocation: 1, offset: 0, format: "float32x2" }] }];
    const legacyLayout = [{ arrayStride: 40, attributes: [
      { shaderLocation: 0, offset: 0, format: "float32x4" }, { shaderLocation: 1, offset: 16, format: "float32x2" }, { shaderLocation: 2, offset: 24, format: "float32x4" },
    ] }, { arrayStride: 32, stepMode: "instance", attributes: [
      { shaderLocation: 3, offset: 0, format: "float32x4" }, { shaderLocation: 4, offset: 16, format: "float32x4" },
    ] }];
    const [rainPipeline, compositePipeline] = await Promise.all([
      device.createRenderPipelineAsync({ layout: "auto",
        vertex: { module, entryPoint: "vertexMain", buffers: native ? nativeLayout : legacyLayout },
        fragment: { module, entryPoint: "fragmentMain", targets: [{ format: "rgba8unorm", blend: { color: native ? sourceAlpha : additive, alpha: native ? sourceAlpha : additive } }] },
        primitive: { topology: "triangle-list" },
      }),
      device.createRenderPipelineAsync({ layout: "auto", vertex: { module: compositeModule, entryPoint: "vertexMain" },
        fragment: { module: compositeModule, entryPoint: "fragmentMain", targets: [{ format }] }, primitive: { topology: "triangle-list" },
      }),
    ]);
    const sceneBuffer = buffer(224, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
    const supplemental = buffer(native ? 48 : 32, GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST);
    device.queue.writeBuffer(supplemental, 0, native ? new Float32Array([0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1]) : new Float32Array(8));
    const compositeBuffer = buffer(32, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
    const sampler = device.createSampler({ minFilter: "linear", magFilter: "linear" });
    const glyphAtlas = imageTexture(atlas, true); atlas.src = "";
    const desktop = imageTexture(runtime.desktop);
    const rainBindGroup = bind(rainPipeline, [{ buffer: sceneBuffer }, sampler, glyphAtlas.createView()]);
    let compositeBindGroup, targetView, geometrySize = 0, width = 0, height = 0;
    device.addEventListener("uncapturederror", event => {
      if (dead) return;
      runtime.diagnostics.errors.push(event.error.message); console.error("UC3D WebGPU validation:", event.error.message); stop?.();
    });
    device.lost.then(info => {
      if (!dead) { runtime.diagnostics.errors.push(`WebGPU device lost: ${info.message}`); stop?.(); }
    });
    stop = startLoop(canvas, runtime, (frame, w, h) => {
      if (w !== width || h !== height) {
        width = w; height = h; canvas.width = w; canvas.height = h;
        target?.destroy(); target = device.createTexture({ size: [w, h], format: "rgba8unorm", usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING });
        targetView = target.createView();
        compositeBindGroup = bind(compositePipeline, [{ buffer: compositeBuffer }, sampler, targetView, desktop.createView()]);
      }
      if (frame.changed) {
        const requiredSize = native ? frame.grid.capacityBytes : frame.grid.data.byteLength;
        if (geometrySize !== requiredSize) {
          geometry?.destroy(); geometrySize = requiredSize;
          geometry = device.createBuffer({ size: geometrySize, usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST });
        }
        if (frame.grid.data.byteLength) device.queue.writeBuffer(geometry, 0, frame.grid.data);
      }
      device.queue.writeBuffer(sceneBuffer, 0, frame.uniforms);
      const blur = Math.max(0, runtime.config.bloomSize ?? 0.4) * 8;
      device.queue.writeBuffer(compositeBuffer, 0, new Float32Array([blur / w, blur / h, blur ? runtime.config.bloomStrength ?? 0.7 : 0, runtime.config.highPassThreshold ?? 0.1, ...runtime.background, runtime.desktop ? 0.72 : 0]));
      const encoder = device.createCommandEncoder();
      const rainPass = encoder.beginRenderPass({ colorAttachments: [{ view: targetView, clearValue: { r: 0, g: 0, b: 0, a: 0 }, loadOp: "clear", storeOp: "store" }] });
      rainPass.setPipeline(rainPipeline); rainPass.setBindGroup(0, rainBindGroup); rainPass.setVertexBuffer(0, geometry);
      rainPass.setVertexBuffer(1, supplemental); rainPass.draw(frame.grid.count, native ? frame.grid.instanceCount : 1); rainPass.end();
      const compositePass = encoder.beginRenderPass({ colorAttachments: [{ view: context.getCurrentTexture().createView(), loadOp: "clear", storeOp: "store", clearValue: { r: 0, g: 0, b: 0, a: 1 } }] });
      compositePass.setPipeline(compositePipeline); compositePass.setBindGroup(0, compositeBindGroup); compositePass.draw(3); compositePass.end();
      device.queue.submit([encoder.finish()]);
    }, cleanup);
    return stop;
  } catch (error) {
    runtime?.diagnostics.errors.push(error.message);
    try { cleanup(); } catch (cleanupError) { console.warn("UC3D WebGPU cleanup failed:", cleanupError); }
    throw error;
  }
}
