#version {VERSION}
#ifdef GL_ES
precision mediump float;
#endif

#define MAX_BEAMS 16

uniform sampler2D WorldTexture;
uniform vec2 BeamStarts[MAX_BEAMS];
uniform vec2 BeamEnds[MAX_BEAMS];
uniform vec3 GlowColors[MAX_BEAMS];
uniform float GlowIntensities[MAX_BEAMS];
uniform float GlowRadii[MAX_BEAMS];
uniform float GlowRadiiEnd[MAX_BEAMS];
uniform float EndpointBoosts[MAX_BEAMS];
uniform float BeamCount;

out vec4 fragColor;

// Returns (distance, t) where t is the clamped projection parameter along [a, b].
vec2 segmentDistT(vec2 p, vec2 a, vec2 b)
{
	vec2 ab = b - a;
	float t = clamp(dot(p - a, ab) / max(dot(ab, ab), 0.0001), 0.0, 1.0);
	return vec2(length(p - (a + t * ab)), t);
}

void main()
{
	vec4 c = texelFetch(WorldTexture, ivec2(gl_FragCoord.xy), 0);
	vec3 rgb = c.rgb;
	int count = int(BeamCount);

	for (int i = 0; i < MAX_BEAMS; ++i)
	{
		if (i >= count)
			break;

		// Radius tapers from GlowRadii (source) to GlowRadiiEnd (target) along the segment,
		// turning a uniform capsule into a cone. EndpointBoosts brightens the wide end into a pool.
		vec2 dt = segmentDistT(gl_FragCoord.xy, BeamStarts[i], BeamEnds[i]);
		float d = dt.x;
		float t = dt.y;
		float r = mix(GlowRadii[i], GlowRadiiEnd[i], t);
		float boost = 1.0 + EndpointBoosts[i] * smoothstep(0.55, 1.0, t);
		float glow = GlowIntensities[i] * boost * exp(-d * d / (r * r));
		vec3 contrib = GlowColors[i] * glow;
		rgb = rgb + contrib * (1.0 - rgb);
	}

	fragColor = vec4(rgb, c.a);
}
