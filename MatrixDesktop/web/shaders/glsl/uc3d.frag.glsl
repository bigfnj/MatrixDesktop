#ifdef GL_OES_standard_derivatives
#extension GL_OES_standard_derivatives : enable
#endif
precision highp float;
uniform sampler2D glyphAtlas;
uniform vec4 glyphOptions;
varying vec2 glyphUV;
varying vec3 glyphColor;
void main() {
  vec3 sampleColor = texture2D(glyphAtlas, glyphUV).rgb;
  float distance = max(min(sampleColor.r, sampleColor.g), min(max(sampleColor.r, sampleColor.g), sampleColor.b));
#ifdef GL_OES_standard_derivatives
  float width = max(fwidth(distance), 0.015);
#else
  float width = 0.06;
#endif
  float mask = smoothstep(0.5 - width, 0.5 + width, distance);
  if (glyphOptions.x > 0.5) mask = max(sampleColor.r, max(sampleColor.g, sampleColor.b));
  gl_FragColor = vec4(glyphColor * mask, mask);
}
