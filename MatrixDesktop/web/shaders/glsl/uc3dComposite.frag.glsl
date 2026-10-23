precision highp float;
uniform sampler2D rain, desktop;
uniform vec4 composition;
uniform vec4 background;
varying vec2 uv;
void main() {
  vec3 color = texture2D(rain, uv).rgb;
  vec2 stepSize = composition.xy;
  vec3 glow = vec3(0.0);
  glow += max(texture2D(rain, uv + stepSize * vec2(1.0, 0.0)).rgb - composition.w, 0.0);
  glow += max(texture2D(rain, uv + stepSize * vec2(-1.0, 0.0)).rgb - composition.w, 0.0);
  glow += max(texture2D(rain, uv + stepSize * vec2(0.0, 1.0)).rgb - composition.w, 0.0);
  glow += max(texture2D(rain, uv + stepSize * vec2(0.0, -1.0)).rgb - composition.w, 0.0);
  glow += max(texture2D(rain, uv + stepSize * vec2(1.0, 1.0)).rgb - composition.w, 0.0) * 0.7;
  glow += max(texture2D(rain, uv + stepSize * vec2(-1.0, 1.0)).rgb - composition.w, 0.0) * 0.7;
  glow += max(texture2D(rain, uv + stepSize * vec2(1.0, -1.0)).rgb - composition.w, 0.0) * 0.7;
  glow += max(texture2D(rain, uv + stepSize * vec2(-1.0, -1.0)).rgb - composition.w, 0.0) * 0.7;
  vec3 base = background.rgb + texture2D(desktop, uv).rgb * background.a;
  gl_FragColor = vec4(base + color + glow * composition.z / 6.8, 1.0);
}
