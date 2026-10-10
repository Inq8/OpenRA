#version {VERSION}
#ifdef GL_ES
precision mediump float;
#endif

#define MAX_DISTORTIONS 16

uniform sampler2D WorldTexture;
uniform vec2 ShockCenters[MAX_DISTORTIONS];
uniform float RingRadii[MAX_DISTORTIONS];
uniform float Strengths[MAX_DISTORTIONS];
uniform float DistortionCount;
uniform float RingThickness;

out vec4 fragColor;

void main()
{
	vec2 fc = gl_FragCoord.xy;
	vec2 sz = vec2(textureSize(WorldTexture, 0));
	int count = int(DistortionCount);

	vec2 offset = vec2(0.0);
	for (int i = 0; i < MAX_DISTORTIONS; ++i)
	{
		if (i >= count)
			break;

		vec2 dir = fc - ShockCenters[i];
		float d = length(dir);

		// Thin gaussian band at the expanding radius; peaks where d == RingRadii[i].
		float band = (d - RingRadii[i]) / RingThickness;
		float ring = exp(-band * band);

		// Radial push outward. Negate for an inward "lens suck".
		if (d > 0.0)
			offset += (dir / d) * Strengths[i] * ring;
	}

	vec2 maxCoord = sz - vec2(1.0);
	ivec2 sampleCoord = ivec2(clamp(fc + offset, vec2(0.0), maxCoord));
	fragColor = texelFetch(WorldTexture, sampleCoord, 0);
}