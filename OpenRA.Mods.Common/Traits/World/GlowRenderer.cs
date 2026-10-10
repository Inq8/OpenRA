#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Renders a screen-space additive glow effect for beams and impacts. Add to the world actor.")]
	public class GlowRendererInfo : TraitInfo
	{
		[Desc("Gaussian falloff radius of the glow in screen pixels.")]
		public readonly float GlowRadius = 60f;

		[Desc("Peak additive intensity of the glow (0-1).")]
		public readonly float GlowIntensity = 0.7f;

		public override object Create(ActorInitializer init) { return new GlowRenderer(this); }
	}

	public sealed class GlowRenderer : IRenderPostProcessPass, INotifyActorDisposing
	{
		const int MaxBeamsPerBatch = 16;

		// Hard upper bound on the number of queued (pending + fading) glows. Draw is the only place
		// these lists are drained, and it runs only on the render tick. If rendering stops (window
		// minimized) or merely falls behind (replay running faster than it can draw), the simulation
		// keeps registering glows with nothing to drain them. Capping the lists means the worst case is
		// a handful of extra shader batches in one frame instead of an unbounded flush that freezes the game.
		const int MaxActiveEffects = 64;

		// Fade timing follows logical game ticks, not render frames, so a glow lasts the same wall-clock
		// duration regardless of framerate (and freezes while the game is paused).
		// The fadeFrames/fadeInFrames values in YAML were tuned as render frames at ReferenceFps; convert
		// them to ticks (at normal-speed sim rate) so the original look is preserved at that framerate.
		const float ReferenceFps = 60f;
		const float TicksPerSecond = 1000f / 40f; // normal game speed = 40ms timestep = 25 ticks/sec
		const float FramesToTicks = TicksPerSecond / ReferenceFps;

		static readonly string[] BeamStartsKeys = Enumerable.Range(0, MaxBeamsPerBatch).Select(i => $"BeamStarts[{i}]").ToArray();
		static readonly string[] BeamEndsKeys = Enumerable.Range(0, MaxBeamsPerBatch).Select(i => $"BeamEnds[{i}]").ToArray();
		static readonly string[] GlowColorsKeys = Enumerable.Range(0, MaxBeamsPerBatch).Select(i => $"GlowColors[{i}]").ToArray();
		static readonly string[] GlowIntensitiesKeys = Enumerable.Range(0, MaxBeamsPerBatch).Select(i => $"GlowIntensities[{i}]").ToArray();
		static readonly string[] GlowRadiiKeys = Enumerable.Range(0, MaxBeamsPerBatch).Select(i => $"GlowRadii[{i}]").ToArray();
		static readonly string[] GlowRadiiEndKeys = Enumerable.Range(0, MaxBeamsPerBatch).Select(i => $"GlowRadiiEnd[{i}]").ToArray();
		static readonly string[] EndpointBoostsKeys = Enumerable.Range(0, MaxBeamsPerBatch).Select(i => $"EndpointBoosts[{i}]").ToArray();

		readonly GlowRendererInfo info;
		readonly Renderer renderer;
		readonly IShader shader;
		readonly IVertexBuffer<RenderPostProcessPassVertex> buffer;

		readonly List<(WPos Source, WPos Target, Color Color, float Scale, float ScaleEnd, float Intensity, float EndpointBoost)> pendingGlows = new();
		readonly List<(WPos Source, WPos Target, Color Color, float Scale, float ScaleEnd, float Intensity, float EndpointBoost, float TicksRemaining, float TotalTicks, float FadeInTicks)> fadingGlows = new();
		readonly Dictionary<WPos, int> glowsPerSource = new();

		// World tick at the previous Draw; used to advance fades by elapsed ticks rather than render frames.
		int lastWorldTick = -1;

		readonly float[] beamStarts = new float[MaxBeamsPerBatch * 2];
		readonly float[] beamEnds = new float[MaxBeamsPerBatch * 2];
		readonly float[] glowColors = new float[MaxBeamsPerBatch * 3];
		readonly float[] glowIntensities = new float[MaxBeamsPerBatch];
		readonly float[] glowRadii = new float[MaxBeamsPerBatch];
		readonly float[] glowRadiiEnd = new float[MaxBeamsPerBatch];
		readonly float[] endpointBoosts = new float[MaxBeamsPerBatch];

		public GlowRenderer(GlowRendererInfo info)
		{
			this.info = info;
			renderer = Game.Renderer;
			shader = renderer.CreateShader(new RenderPostProcessPassShaderBindings("glow"));
			buffer = renderer.CreateVertexBuffer(new RenderPostProcessPassVertex[]
			{
				new(-1, -1), new(1, -1), new(1, 1),
				new(1, 1), new(-1, 1), new(-1, -1)
			}, false);
		}

		// intensity is a brightness-only multiplier (independent of scale, which also drives radius).
		// scaleEnd tapers the radius from scale (at source) to scaleEnd (at target) to form a cone;
		// pass -1 to keep a uniform-radius beam. endpointBoost brightens the wide end into a pool.
		public void RegisterGlow(WPos source, WPos target, Color color, float scale = 1f, int fadeFrames = 0, int fadeInFrames = 0, float intensity = 1f, float scaleEnd = -1f, float endpointBoost = 0f)
		{
			// Render-only cosmetic state that is drained exclusively by Draw (render tick). While the
			// window is minimized the render tick never runs, so nothing drains these lists, yet the
			// simulation keeps detonating warheads and ticking searchlights that call this. Dropping the
			// registration while suspended is what keeps the lists from growing without bound and flushing
			// in one frame on restore. Sync is unaffected: the simulation never reads this state.
			if (Game.Renderer.WindowIsSuspended)
				return;

			if (scaleEnd < 0f)
				scaleEnd = scale;

			// If no glow is currently active, Draw has been idle (it only runs while effects exist) so
			// lastWorldTick is stale; reset it, otherwise the first Draw would advance the fade by the whole
			// idle gap and instantly expire this glow.
			if (pendingGlows.Count == 0 && fadingGlows.Count == 0)
				lastWorldTick = -1;

			if (fadeFrames > 0)
			{
				// Defensive bound for the render-starved (not suspended) case: drop the oldest, most-faded
				// glow rather than let the list grow without limit.
				if (fadingGlows.Count >= MaxActiveEffects)
					fadingGlows.RemoveAt(0);

				var totalTicks = fadeFrames * FramesToTicks;
				var fadeInTicks = fadeInFrames * FramesToTicks;
				fadingGlows.Add((source, target, color, scale, scaleEnd, intensity, endpointBoost, totalTicks, totalTicks, fadeInTicks));
				return;
			}

			// Cap at 2 simultaneous beam glows per source position per frame.
			// Rapid-fire weapons that produce more than 2 beams per tick skip the extras.
			glowsPerSource.TryGetValue(source, out var count);
			if (count >= 2)
				return;

			if (pendingGlows.Count >= MaxActiveEffects)
				return;

			glowsPerSource[source] = count + 1;
			pendingGlows.Add((source, target, color, scale, scaleEnd, intensity, endpointBoost));
		}

		PostProcessPassType IRenderPostProcessPass.Type => PostProcessPassType.AfterActors;
		bool IRenderPostProcessPass.Enabled => pendingGlows.Count > 0 || fadingGlows.Count > 0;

		void IRenderPostProcessPass.Draw(WorldRenderer wr)
		{
			var downscale = renderer.WorldDownscaleFactor;
			var topLeft = wr.Viewport.TopLeft;

			float2 ToFb(WPos pos)
			{
				var screenPx = wr.ScreenPxPosition(pos);
				return new float2(
					(screenPx.X - topLeft.X) * downscale,
					(screenPx.Y - topLeft.Y) * downscale);
			}

			// Collect all glows for this frame into one flat list so they can be batched together.
			var batch = new List<(WPos Source, WPos Target, Color Color, float Scale, float ScaleEnd, float Intensity, float EndpointBoost)>(pendingGlows.Count + fadingGlows.Count);
			foreach (var g in pendingGlows)
				batch.Add(g);
			pendingGlows.Clear();
			glowsPerSource.Clear();

			// Advance fades by the number of game ticks since the last Draw. At high framerates multiple
			// frames render per tick (elapsed == 0, glow holds steady); while paused WorldTick is frozen.
			var worldTick = wr.World.WorldTick;
			var ticksElapsed = lastWorldTick < 0 ? 0f : Math.Max(0, worldTick - lastWorldTick);
			lastWorldTick = worldTick;

			for (var i = fadingGlows.Count - 1; i >= 0; i--)
			{
				var glow = fadingGlows[i];
				var ticksPassed = glow.TotalTicks - glow.TicksRemaining;
				float fadeScale;
				if (glow.FadeInTicks > 0 && ticksPassed < glow.FadeInTicks)
					fadeScale = ticksPassed / glow.FadeInTicks;
				else
				{
					var fadeOutTotal = glow.TotalTicks - glow.FadeInTicks;
					fadeScale = fadeOutTotal > 0 ? glow.TicksRemaining / fadeOutTotal : 1f;
				}

				fadeScale = Math.Clamp(fadeScale, 0f, 1f);
				batch.Add((glow.Source, glow.Target, glow.Color, glow.Scale * fadeScale, glow.ScaleEnd * fadeScale, glow.Intensity, glow.EndpointBoost));

				var remaining = glow.TicksRemaining - ticksElapsed;
				if (remaining <= 0f)
					fadingGlows.RemoveAt(i);
				else
					fadingGlows[i] = (glow.Source, glow.Target, glow.Color, glow.Scale, glow.ScaleEnd, glow.Intensity, glow.EndpointBoost, remaining, glow.TotalTicks, glow.FadeInTicks);
			}

			// Draw glows in fixed-size batches. Each batch takes one framebuffer snapshot and runs
			// a single shader pass that loops over all beams, applying screen-blend math iteratively.
			for (var offset = 0; offset < batch.Count; offset += MaxBeamsPerBatch)
			{
				var batchSize = Math.Min(MaxBeamsPerBatch, batch.Count - offset);

				for (var i = 0; i < batchSize; i++)
				{
					var g = batch[offset + i];
					var p1 = ToFb(g.Source);
					var p2 = ToFb(g.Target);

					beamStarts[i * 2] = p1.X;
					beamStarts[i * 2 + 1] = p1.Y;
					beamEnds[i * 2] = p2.X;
					beamEnds[i * 2 + 1] = p2.Y;
					glowColors[i * 3] = g.Color.R / 255f;
					glowColors[i * 3 + 1] = g.Color.G / 255f;
					glowColors[i * 3 + 2] = g.Color.B / 255f;

					// Brightness uses the average scale so a tapered cone is not dimmed by its narrow source;
					// for uniform beams (Scale == ScaleEnd) this is identical to the source scale.
					var brightnessScale = (g.Scale + g.ScaleEnd) * 0.5f;
					glowIntensities[i] = info.GlowIntensity * brightnessScale * g.Intensity * (g.Color.A / 255f);
					glowRadii[i] = info.GlowRadius * g.Scale;
					glowRadiiEnd[i] = info.GlowRadius * g.ScaleEnd;
					endpointBoosts[i] = g.EndpointBoost;
				}

				shader.SetTexture("WorldTexture", Game.Renderer.WorldBufferSnapshot());

				// ANGLE/ES rejects glUniformXfv with count > 1 on array uniforms, so set each element individually.
				for (var i = 0; i < batchSize; i++)
				{
					shader.SetVec(BeamStartsKeys[i], beamStarts[i * 2], beamStarts[i * 2 + 1]);
					shader.SetVec(BeamEndsKeys[i], beamEnds[i * 2], beamEnds[i * 2 + 1]);
					shader.SetVec(GlowColorsKeys[i], glowColors[i * 3], glowColors[i * 3 + 1], glowColors[i * 3 + 2]);
					shader.SetVec(GlowIntensitiesKeys[i], glowIntensities[i]);
					shader.SetVec(GlowRadiiKeys[i], glowRadii[i]);
					shader.SetVec(GlowRadiiEndKeys[i], glowRadiiEnd[i]);
					shader.SetVec(EndpointBoostsKeys[i], endpointBoosts[i]);
				}

				shader.SetVec("BeamCount", (float)batchSize);
				shader.PrepareRender();
				renderer.DrawBatch(buffer, shader, 0, 6, PrimitiveType.TriangleList);
			}
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			buffer.Dispose();
		}
	}
}
