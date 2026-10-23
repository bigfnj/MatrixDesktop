precision highp float;
attribute vec4 cell;
attribute vec2 corner;
attribute vec4 photo;
attribute vec4 origin, glyphState;
uniform mat4 projection;
uniform vec4 scene0, scene1, scene2, scene3, scene4, scene5, scene6, codeColor, cursorColor;
uniform vec4 glyphOptions;
varying vec2 glyphUV;
varying vec3 glyphColor;
varying float glyphAlpha;

void originalGlyph() {
  float halfSize = glyphOptions.z;
  vec3 p = origin.xyz + vec3((corner.x - 0.5) * 2.0 * halfSize / max(scene6.y, 0.1),
    (cell.y * scene6.z + corner.y - 0.5) * 2.0 * halfSize, 0.0);
  float weight = scene2.z * photo.a;
  float luminance = (photo.r + photo.g + photo.b) / 3.0;
  if (scene2.w > 0.5 && photo.a > 0.0) p.z = luminance * 3.0 * scene4.x;
  gl_Position = projection * vec4(p, 1.0);
  vec3 rainColor = cell.y < 0.5 ? cursorColor.rgb * scene4.w * 0.5 : codeColor.rgb * scene4.z;
  vec3 pictureColor = codeColor.rgb * luminance;
  if (scene4.y < 0.5) pictureColor = mix(pictureColor, photo.rgb, weight);
  glyphColor = photo.a > 0.0 ? pictureColor * scene4.z : rainColor;
  float alpha = glyphState.z * glyphState.w;
  if (scene2.w < 0.5 && scene3.w > 0.5) alpha *= cell.w;
  glyphAlpha = photo.a > 0.0 ? 1.0 : clamp(alpha, 0.0, 1.0);
  float symbol = mod(glyphState.x, scene3.z);
  vec2 local = corner - 0.5;
  if (glyphState.y > 0.5) local.x *= -1.0;
  if (scene5.z > 0.5) local.x *= -1.0;
  float c = cos(scene5.w), s = sin(scene5.w);
  local = mat2(c, s, -s, c) * local * clamp(1.0 - scene6.w, 0.0, 1.0);
  vec2 tile = vec2(mod(symbol, scene5.x), scene5.y - floor(symbol / scene5.x) - 1.0);
  glyphUV = (local + 0.5 + tile) / scene5.xy;
}

vec3 rotateScene(vec3 p) {
  float cy = cos(scene1.z), sy = sin(scene1.z), cp = cos(scene1.w), sp = sin(scene1.w);
  float cr = cos(scene2.x), sr = sin(scene2.x);
  p.z += 25.0;
  p.xy = mat2(cr, sr, -sr, cr) * p.xy;
  p.yz = mat2(cp, sp, -sp, cp) * p.yz;
  p.xz = mat2(cy, -sy, sy, cy) * p.xz;
  p.z -= 25.0;
  return p;
}
void main() {
  if (glyphOptions.y > 0.5) { originalGlyph(); return; }
  glyphAlpha = 1.0;
  float aspect = scene0.x, rows = scene0.y, columns = scene0.z;
  vec2 center = vec2((cell.x + 0.5) / columns * 2.0 - 1.0, 1.0 - (cell.y + 0.5) / rows * 2.0);
  vec2 offset = (corner - 0.5) * vec2(2.0 * aspect / scene0.w, 2.0 / rows) * 0.93;
  offset.x /= max(scene6.y, 0.1); offset.y *= scene6.z;
  float weight = scene2.z * photo.a;
  float luminance = dot(photo.rgb, vec3(0.2126, 0.7152, 0.0722));
  // Recovered native units: eye z=25, world wrap +/-25, half glyph size
  // GlyphSize*.01 (.15 at default15), speed 2+rand%(FallSpeed+30)*.1.
  // Compatibility controls: halfSize=12/columns and native FallSpeed=fallSpeed*40/.3.
  float halfSize = 12.0 / scene0.w;
  float nativeFall = abs(scene3.x) * 40.0 / 0.3;
  float speed = (2.0 + floor(fract(cell.z * 19.31) * (nativeFall + 30.0)) * 0.1) * sign(scene3.x);
  float headY = 25.0 - mod(scene1.x * speed + cell.z * 50.0, 50.0);
  float streamY = headY + cell.y * halfSize * 2.0 * scene6.z;
  float randomX = floor(fract(cell.z * 37.13) * 5000.0) * 0.01 - 25.0;
  vec3 p = vec3(randomX / 25.0 * aspect, streamY / 25.0, 0.0);
  p.xy += (corner - 0.5) * halfSize * 2.0 / 25.0 * vec2(1.0 / max(scene6.y, 0.1), 1.0);
  if (scene2.w > 0.5) {
    vec3 rainPosition = vec3(randomX, streamY, -25.0 + fract(cell.z * 13.71 + scene1.y) * 50.0);
    rainPosition.xy += (corner - 0.5) * halfSize * 2.0 * vec2(1.0 / max(scene6.y, 0.1), 1.0);
    vec3 picturePosition = vec3((center * vec2(aspect, 1.0) + offset) * 14.4337567, luminance * 3.0 * clamp(scene4.x, 0.0, 4.0));
    p = mix(rainPosition, picturePosition, weight);
    p.x += scene2.y * (1.0 - scene2.z);
    p.z -= 25.0;
    p = rotateScene(p);
  } else {
    p.xy = mix(p.xy, center * vec2(aspect, 1.0) + offset, weight);
    p.x += scene2.y / 25.0 * (1.0 - scene2.z);
  }
  gl_Position = projection * vec4(p, 1.0);
  float length = clamp((10.0 + floor(fract(cell.z * 53.7) * 30.0)) * scene6.x / 0.75, 1.0, rows);
  float light = clamp((length - cell.y) / 8.0, 0.0, 1.0);
  float head = 1.0 - step(0.5, cell.y);
  if (scene2.w < 0.5 && scene3.w > 0.5) light *= cell.w;
  vec3 photoColor = scene4.y > 0.5 ? codeColor.rgb * luminance * 1.3 : photo.rgb;
  vec3 rainColor = codeColor.rgb * light * scene4.z + cursorColor.rgb * head * scene4.w * 0.55;
  glyphColor = mix(rainColor, photoColor * scene4.z, weight);
  float cycle = floor(scene1.x * scene3.y * 30.0);
  float symbol = mod(floor(cell.z * scene3.z) + cell.y * 7.0 + cycle * (1.0 + floor(cell.z * 13.0)), scene3.z);
  vec2 local = corner - 0.5;
  if (mod(floor(cell.z * 1000.0) + cell.y * 13.0, 2.0) > 0.5) local.x *= -1.0;
  if (scene5.z > 0.5) local.x *= -1.0;
  float c = cos(scene5.w), s = sin(scene5.w);
  local = mat2(c, s, -s, c) * local;
  local *= clamp(1.0 - scene6.w, 0.0, 1.0);
  vec2 tile = vec2(mod(symbol, scene5.x), scene5.y - floor(symbol / scene5.x) - 1.0);
  glyphUV = (local + 0.5 + tile) / scene5.xy;
}
