struct Composition { settings: vec4<f32>, background: vec4<f32> };
@group(0) @binding(0) var<uniform> composition: Composition;
@group(0) @binding(1) var linearSampler: sampler;
@group(0) @binding(2) var rain: texture_2d<f32>;
@group(0) @binding(3) var desktop: texture_2d<f32>;
struct VertexOutput { @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32> };
@vertex fn vertexMain(@builtin(vertex_index) index: u32) -> VertexOutput {
  let positions = array<vec2<f32>,3>(vec2<f32>(-1.0,-1.0),vec2<f32>(3.0,-1.0),vec2<f32>(-1.0,3.0));
  let p = positions[index];
  return VertexOutput(vec4<f32>(p,0.0,1.0),vec2<f32>((p.x+1.0)*0.5,(1.0-p.y)*0.5));
}
fn bright(uv: vec2<f32>) -> vec3<f32> { return max(textureSample(rain,linearSampler,uv).rgb-composition.settings.w,vec3<f32>(0.0)); }
@fragment fn fragmentMain(input: VertexOutput) -> @location(0) vec4<f32> {
  let uv = input.uv; let stepSize = composition.settings.xy;
  let color = textureSample(rain,linearSampler,uv).rgb;
  var glow = bright(uv+stepSize*vec2<f32>(1.0,0.0))+bright(uv+stepSize*vec2<f32>(-1.0,0.0));
  glow += bright(uv+stepSize*vec2<f32>(0.0,1.0))+bright(uv+stepSize*vec2<f32>(0.0,-1.0));
  glow += (bright(uv+stepSize*vec2<f32>(1.0,1.0))+bright(uv+stepSize*vec2<f32>(-1.0,1.0)))*0.7;
  glow += (bright(uv+stepSize*vec2<f32>(1.0,-1.0))+bright(uv+stepSize*vec2<f32>(-1.0,-1.0)))*0.7;
  let base = composition.background.rgb+textureSample(desktop,linearSampler,uv).rgb*composition.background.a;
  return vec4<f32>(base+color+glow*composition.settings.z/6.8,1.0);
}
