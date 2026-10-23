struct Scene {
  projection: mat4x4<f32>,
  scene0: vec4<f32>, scene1: vec4<f32>, scene2: vec4<f32>, scene3: vec4<f32>,
  scene4: vec4<f32>, scene5: vec4<f32>, scene6: vec4<f32>,
  codeColor: vec4<f32>, cursorColor: vec4<f32>, reserved: vec4<f32>,
};
@group(0) @binding(0) var<uniform> scene: Scene;
@group(0) @binding(1) var atlasSampler: sampler;
@group(0) @binding(2) var atlas: texture_2d<f32>;
struct VertexInput { @location(0) cell: vec4<f32>, @location(1) corner: vec2<f32>, @location(2) photo: vec4<f32> };
struct VertexOutput { @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32>, @location(1) color: vec3<f32> };
fn rotateScene(point: vec3<f32>) -> vec3<f32> {
  var p = point;
  let cy = cos(scene.scene1.z); let sy = sin(scene.scene1.z); let cp = cos(scene.scene1.w); let sp = sin(scene.scene1.w);
  let cr = cos(scene.scene2.x); let sr = sin(scene.scene2.x);
  p.z += 25.0;
  p = vec3<f32>(cr*p.x-sr*p.y, sr*p.x+cr*p.y, p.z);
  p = vec3<f32>(p.x, cp*p.y-sp*p.z, sp*p.y+cp*p.z);
  p = vec3<f32>(cy*p.x+sy*p.z, p.y, -sy*p.x+cy*p.z);
  p.z -= 25.0;
  return p;
}
@vertex fn vertexMain(input: VertexInput) -> VertexOutput {
  let cell = input.cell; let corner = input.corner; let photo = input.photo;
  let aspect = scene.scene0.x; let rows = scene.scene0.y; let columns = scene.scene0.z;
  let center = vec2<f32>((cell.x+0.5)/columns*2.0-1.0, 1.0-(cell.y+0.5)/rows*2.0);
  var offset = (corner-0.5)*vec2<f32>(2.0*aspect/scene.scene0.w, 2.0/rows)*0.93;
  offset.x /= max(scene.scene6.y, 0.1); offset.y *= scene.scene6.z;
  let weight = scene.scene2.z*photo.a;
  let luminance = dot(photo.rgb,vec3<f32>(0.2126,0.7152,0.0722));
  let halfSize = 12.0/scene.scene0.w;
  let nativeFall = abs(scene.scene3.x)*40.0/0.3;
  let speed = (2.0+floor(fract(cell.z*19.31)*(nativeFall+30.0))*0.1)*sign(scene.scene3.x);
  let rawHead = scene.scene1.x*speed+cell.z*50.0;
  let headY = 25.0-(rawHead-floor(rawHead/50.0)*50.0);
  let streamY = headY+cell.y*halfSize*2.0*scene.scene6.z;
  let randomX = floor(fract(cell.z*37.13)*5000.0)*0.01-25.0;
  var p = vec3<f32>(randomX/25.0*aspect,streamY/25.0,0.0);
  p = vec3<f32>(p.xy+(corner-0.5)*halfSize*2.0/25.0*vec2<f32>(1.0/max(scene.scene6.y,0.1),1.0),p.z);
  if (scene.scene2.w > 0.5) {
    var rainPosition = vec3<f32>(randomX,streamY,-25.0+fract(cell.z*13.71+scene.scene1.y)*50.0);
    rainPosition = vec3<f32>(rainPosition.xy+(corner-0.5)*halfSize*2.0*vec2<f32>(1.0/max(scene.scene6.y,0.1),1.0),rainPosition.z);
    let picturePosition = vec3<f32>((center*vec2<f32>(aspect,1.0)+offset)*14.4337567,luminance*3.0*clamp(scene.scene4.x,0.0,4.0));
    p = mix(rainPosition,picturePosition,weight);
    p.x += scene.scene2.y*(1.0-scene.scene2.z);
    p.z -= 25.0;
    p = rotateScene(p);
  } else {
    p = vec3<f32>(mix(p.xy,center*vec2<f32>(aspect,1.0)+offset,weight),p.z);
    p.x += scene.scene2.y/25.0*(1.0-scene.scene2.z);
  }
  let length = clamp((10.0+floor(fract(cell.z*53.7)*30.0))*scene.scene6.x/0.75,1.0,rows);
  var light = clamp((length-cell.y)/8.0,0.0,1.0);
  let head = 1.0-step(0.5,cell.y);
  if (scene.scene2.w < 0.5 && scene.scene3.w > 0.5) { light *= cell.w; }
  let photoColor = select(photo.rgb,scene.codeColor.rgb*luminance*1.3,scene.scene4.y>0.5);
  let rainColor = scene.codeColor.rgb*light*scene.scene4.z+scene.cursorColor.rgb*head*scene.scene4.w*0.55;
  let color = mix(rainColor,photoColor*scene.scene4.z,weight);
  let cycle = floor(scene.scene1.x*scene.scene3.y*30.0);
  let rawSymbol = floor(cell.z*scene.scene3.z)+cell.y*7.0+cycle*(1.0+floor(cell.z*13.0));
  let symbol = rawSymbol-floor(rawSymbol/scene.scene3.z)*scene.scene3.z;
  var local = corner-0.5;
  if ((u32(floor(cell.z*1000.0))+u32(cell.y)*13u)%2u>0u) { local.x *= -1.0; }
  if (scene.scene5.z > 0.5) { local.x *= -1.0; }
  let c = cos(scene.scene5.w); let s = sin(scene.scene5.w);
  local = vec2<f32>(c*local.x-s*local.y,s*local.x+c*local.y)*clamp(1.0-scene.scene6.w,0.0,1.0);
  let tile = vec2<f32>(symbol-floor(symbol/scene.scene5.x)*scene.scene5.x,scene.scene5.y-floor(symbol/scene.scene5.x)-1.0);
  return VertexOutput(scene.projection*vec4<f32>(p,1.0),(local+0.5+tile)/scene.scene5.xy,color);
}
@fragment fn fragmentMain(input: VertexOutput) -> @location(0) vec4<f32> {
  let color = textureSample(atlas,atlasSampler,input.uv).rgb;
  let distance = max(min(color.r,color.g),min(max(color.r,color.g),color.b));
  let width = max(fwidth(distance),0.015);
  var mask = smoothstep(0.5-width,0.5+width,distance);
  if (scene.reserved.x > 0.5) { mask = max(color.r,max(color.g,color.b)); }
  return vec4<f32>(input.color*mask,mask);
}
