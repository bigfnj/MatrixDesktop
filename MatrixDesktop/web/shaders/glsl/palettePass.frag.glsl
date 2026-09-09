precision mediump float;
#define PI 3.14159265359

uniform sampler2D tex;
uniform sampler2D bloomTex;
uniform sampler2D paletteTex;
uniform float ditherMagnitude;
uniform float time;
uniform vec3 backgroundColor, cursorColor, glintColor;
uniform float cursorIntensity, glintIntensity;
// Scales the glyph colour, completing the set: brightness.r is the glyph channel, .g the
// cursor, .b the glint, and the other two already had an intensity. Deliberately a bare
// multiply with no min() clamp, unlike the cursor and glint terms: at the default of 1.0
// `x * 1.0` is bit-identical to `x` for every float, so adding this cannot change a single
// pixel of an existing render, whereas introducing a clamp here would.
uniform float glyphIntensity;
varying vec2 vUV;

highp float rand( const in vec2 uv, const in float t ) {
	const highp float a = 12.9898, b = 78.233, c = 43758.5453;
	highp float dt = dot( uv.xy, vec2( a,b ) ), sn = mod( dt, PI );
	return fract(sin(sn) * c + t);
}

vec4 getBrightness(vec2 uv) {
	vec4 primary = texture2D(tex, uv);
	vec4 bloom = texture2D(bloomTex, uv);
	return primary + bloom;
}

void main() {
	vec4 brightness = getBrightness(vUV);

	// Dither: subtract a random value from the brightness
	brightness -= rand( gl_FragCoord.xy, time ) * ditherMagnitude / 3.0;

	// Map the brightness to a position in the palette texture
	gl_FragColor = vec4(
		texture2D( paletteTex, vec2(brightness.r, 0.0)).rgb * glyphIntensity
			+ min(cursorColor * cursorIntensity * brightness.g, vec3(1.0))
			+ min(glintColor * glintIntensity * brightness.b, vec3(1.0))
			+ backgroundColor,
		1.0
	);
}
