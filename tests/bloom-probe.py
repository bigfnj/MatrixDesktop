"""GPU readback witnesses for the two alleged upstream bloom defects.

Runs on the installed Playwright Chromium using a fresh browser profile. It reads no
user browser profile and returns 2 when WebGPU cannot be measured.
"""
import functools
import http.server
import json
from pathlib import Path
import sys
import threading

from playwright.sync_api import sync_playwright

WEB = Path(__file__).resolve().parents[1] / "MatrixDesktop" / "web"

class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path == "/__probe":
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.end_headers()
            self.wfile.write(b"<!doctype html><title>Bloom probe</title>")
        else:
            super().do_GET()

WEBGPU = r"""async () => {
    const adapter = await navigator.gpu?.requestAdapter();
    if (!adapter) return {available: false};
    const device = await adapter.requestDevice();
    const errors = [];
    device.addEventListener('uncapturederror', (event) => errors.push(event.error.message));
    const {structs} = await import('/lib/gpu-buffer.js');
    const {makeUniformBuffer, makeBindGroup} = await import('/js/webgpu/utils.js');
    const original = await (await fetch('/shaders/wgsl/bloomCombine.wgsl')).text();
    const buffer = makeUniformBuffer(device, structs.from(original).Config, {pyramidHeight: 4, bloomStrength: 0.2});
    const textures = [];
    for (let level = 0; level < 4; level++) {
        const w = 64 >> level, h = 32 >> level;
        const tex = device.createTexture({size: [w,h], format:'rgba8unorm', mipLevelCount:1, usage:GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST});
        const data = new Uint8Array(w*h*4);
        for (let y=0;y<h;y++) for (let x=0;x<w;x++) {
            const p=(y*w+x)*4;
            data[p]=3+level*7+x%11; data[p+1]=5+level*5+y%7; data[p+2]=2+level*6+(x+y)%9; data[p+3]=255;
        }
        device.queue.writeTexture({texture:tex},data,{bytesPerRow:w*4},[w,h]);
        textures.push(tex);
    }
    const sampler=device.createSampler({minFilter:'linear',magFilter:'linear'});
    const sample = async (code) => {
        const shader=device.createShaderModule({code});
        const pipeline=await device.createComputePipelineAsync({layout:'auto',compute:{module:shader,entryPoint:'computeMain'}});
        const output=device.createTexture({size:[64,32],format:'rgba8unorm',usage:GPUTextureUsage.STORAGE_BINDING|GPUTextureUsage.COPY_SRC});
        const group=makeBindGroup(device,pipeline,0,[buffer,sampler,...textures.map(t=>t.createView()),output.createView()]);
        const readback=device.createBuffer({size:64*32*4,usage:GPUBufferUsage.MAP_READ|GPUBufferUsage.COPY_DST});
        const encoder=device.createCommandEncoder();
        const pass=encoder.beginComputePass(); pass.setPipeline(pipeline); pass.setBindGroup(0,group); pass.dispatchWorkgroups(2,32); pass.end();
        encoder.copyTextureToBuffer({texture:output},{buffer:readback,bytesPerRow:256},[64,32]);
        device.queue.submit([encoder.finish()]);
        await readback.mapAsync(GPUMapMode.READ);
        const bytes=new Uint8Array(readback.getMappedRange()).slice();
        readback.unmap(); readback.destroy(); output.destroy(); return bytes;
    };
    const before=await sample(original);
    const zero=await sample(original.replaceAll('i + 1.0','0.0'));
    const mutant=await sample(original.replace('tex1, linearSampler, uv, i + 1.0 ) * weight','tex1, linearSampler, uv, i + 1.0 ) * weight * 0.5'));
    const differences=(a,b)=>a.reduce((count,v,i)=>count+(v!==b[i]),0);
    const result={available:true,bytes:before.length,lod0DifferentBytes:differences(before,zero),witnessDifferentBytes:differences(before,mutant),errors};
    textures.forEach(t=>t.destroy()); buffer.destroy(); device.destroy(); return result;
}"""

REGL = r"""async () => {
    const {makePassFBO} = await import('/js/regl/utils.js');
    const canvas=document.createElement('canvas'); canvas.width=37;canvas.height=23;
    const regl=createREGL({canvas,extensions:['OES_texture_half_float','OES_texture_half_float_linear'],optionalExtensions:['EXT_color_buffer_half_float','WEBGL_color_buffer_float']});
    const data=new Uint8Array(37*23*4);
    for(let i=0;i<data.length;i++)data[i]=(i*17)%128;
    const primary=regl.texture({width:37,height:23,data,format:'rgba'});
    const output=regl.framebuffer({width:37,height:23,depthStencil:false});
    const draw=regl({vert:'precision mediump float;attribute vec2 p;varying vec2 uv;void main(){uv=p*.5+.5;gl_Position=vec4(p,0,1);}',frag:'precision mediump float;varying vec2 uv;uniform sampler2D image,bloom;void main(){gl_FragColor=texture2D(image,uv)+texture2D(bloom,uv);}',attributes:{p:[[-1,-1],[3,-1],[-1,3]]},count:3,uniforms:{image:primary,bloom:regl.prop('bloom')},framebuffer:output});
    const sample=(bloom)=>{draw({bloom});return regl.read({framebuffer:output});};
    const old=makePassFBO(regl,false), matching=makePassFBO(regl,true);
    const before=sample(old), after=sample(matching);
    const white=regl.texture({data:[255,255,255,255],width:1,height:1,format:'rgba'});
    const mutant=sample(white);
    const differences=(a,b)=>a.reduce((count,v,i)=>count+(v!==b[i]),0);
    const result={bytes:before.length,halfFloatDifferentBytes:differences(before,after),witnessDifferentBytes:differences(before,mutant),oldSize:[old.width,old.height],matchingSize:[matching.width,matching.height]};
    regl.destroy();return result;
}"""

def main():
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, directory=str(WEB)))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        with sync_playwright() as pw:
            flags = ["--enable-unsafe-webgpu"]
            if "--hardware" not in sys.argv:
                flags += ["--use-gl=angle", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"]
            channel = sys.argv[sys.argv.index("--channel") + 1] if "--channel" in sys.argv else None
            browser = pw.chromium.launch(args=flags, channel=channel)
            page = browser.new_page()
            page.goto(f"http://127.0.0.1:{server.server_port}/__probe")
            page.add_script_tag(url=f"http://127.0.0.1:{server.server_port}/lib/regl.min.js")
            webgpu = page.evaluate(WEBGPU)
            regl = page.evaluate(REGL)
            print(json.dumps({"webgpu": webgpu, "regl": regl}, indent=2))
            browser.close()
            assert regl["halfFloatDifferentBytes"] == 0 and regl["witnessDifferentBytes"] > 0
            if not webgpu["available"]:
                print("UNCHECKED WebGPU readback unavailable")
                return 2
            assert webgpu["lod0DifferentBytes"] == 0 and webgpu["witnessDifferentBytes"] > 0 and not webgpu["errors"]
            return 0
    finally:
        server.shutdown()

if __name__ == "__main__":
    sys.exit(main())
