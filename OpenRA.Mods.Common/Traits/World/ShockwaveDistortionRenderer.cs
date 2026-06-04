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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Renders a screen-space shockwave-lens distortion: a refraction ring that expands outward",
		"from an impact over a fraction of a second (pressure-wave look). Add to the world actor.",
		"Deliberately separate from HeatDistortionRenderer: heat is vertical/ambient, this is radial/transient.")]
	public class ShockwaveDistortionRendererInfo : TraitInfo
	{
		[Desc("Screen pixels the ring expands out to over its lifetime.")]
		public readonly float MaxRadius = 300f;

		[Desc("Thickness in screen pixels of the displaced ring band.")]
		public readonly float RingThickness = 30f;

		[Desc("Peak radial pixel displacement at the ring band.")]
		public readonly float Strength = 20f;

		public override object Create(ActorInitializer init) { return new ShockwaveDistortionRenderer(this); }
	}

	public sealed class ShockwaveDistortionRenderer : IRenderPostProcessPass, INotifyActorDisposing
	{
		const int MaxDistortionsPerBatch = 16;

		static readonly string[] CentersKeys = Enumerable.Range(0, MaxDistortionsPerBatch).Select(i => $"ShockCenters[{i}]").ToArray();
		static readonly string[] RadiiKeys = Enumerable.Range(0, MaxDistortionsPerBatch).Select(i => $"RingRadii[{i}]").ToArray();
		static readonly string[] StrengthsKeys = Enumerable.Range(0, MaxDistortionsPerBatch).Select(i => $"Strengths[{i}]").ToArray();

		readonly ShockwaveDistortionRendererInfo info;
		readonly Renderer renderer;
		readonly IShader shader;
		readonly IVertexBuffer<RenderPostProcessPassVertex> buffer;

		readonly List<(WPos Center, float Scale)> pendingDistortions = new();
		readonly List<(WPos Center, float Scale, int FramesRemaining, int TotalFrames, int FadeInFrames)> fadingDistortions = new();

		readonly float[] centers = new float[MaxDistortionsPerBatch * 2];
		readonly float[] radii = new float[MaxDistortionsPerBatch];
		readonly float[] strengths = new float[MaxDistortionsPerBatch];

		public ShockwaveDistortionRenderer(ShockwaveDistortionRendererInfo info)
		{
			this.info = info;
			renderer = Game.Renderer;
			shader = renderer.CreateShader(new RenderPostProcessPassShaderBindings("shockwave"));
			buffer = renderer.CreateVertexBuffer(new RenderPostProcessPassVertex[]
			{
				new(-1, -1), new(1, -1), new(1, 1),
				new(1, 1), new(-1, 1), new(-1, -1)
			}, false);
		}

		public void RegisterShockwave(WPos center, float scale = 1f, int fadeFrames = 0, int fadeInFrames = 0)
		{
			if (fadeFrames > 0)
			{
				fadingDistortions.Add((center, scale, fadeFrames, fadeFrames, fadeInFrames));
				return;
			}

			pendingDistortions.Add((center, scale));
		}

		PostProcessPassType IRenderPostProcessPass.Type => PostProcessPassType.AfterActors;

		bool IRenderPostProcessPass.Enabled => pendingDistortions.Count > 0 || fadingDistortions.Count > 0;

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

			// Collect all shockwaves for this frame into one flat list (Center, Scale, Progress) so they can
			// be batched together. Progress 0->1 drives the ring's expanding radius and its fade.
			var batch = new List<(WPos Center, float Scale, float Progress)>(pendingDistortions.Count + fadingDistortions.Count);
			foreach (var d in pendingDistortions)
				batch.Add((d.Center, d.Scale, 0f));
			pendingDistortions.Clear();

			for (var i = fadingDistortions.Count - 1; i >= 0; i--)
			{
				var d = fadingDistortions[i];
				var framesPassed = d.TotalFrames - d.FramesRemaining;

				// progress: how far the ring is through its lifetime (0 = just born, 1 = fully expanded/gone).
				var progress = (float)framesPassed / d.TotalFrames;

				// Optional ease-in on intensity (FadeInFrames). Most shockwaves use 0.
				var fadeIn = d.FadeInFrames > 0 && framesPassed < d.FadeInFrames
					? (float)framesPassed / d.FadeInFrames
					: 1f;

				batch.Add((d.Center, d.Scale * fadeIn, progress));

				if (d.FramesRemaining <= 1)
					fadingDistortions.RemoveAt(i);
				else
					fadingDistortions[i] = (d.Center, d.Scale, d.FramesRemaining - 1, d.TotalFrames, d.FadeInFrames);
			}

			// Draw shockwaves in fixed-size batches. Each batch takes one framebuffer snapshot and runs a
			// single shader pass that loops over all rings, accumulating radial displacement before sampling.
			for (var offset = 0; offset < batch.Count; offset += MaxDistortionsPerBatch)
			{
				var batchSize = Math.Min(MaxDistortionsPerBatch, batch.Count - offset);

				for (var i = 0; i < batchSize; i++)
				{
					var d = batch[offset + i];
					var p = ToFb(d.Center);

					centers[i * 2] = p.X;
					centers[i * 2 + 1] = p.Y;

					// Per-ring animated radius: expands from 0 to MaxRadius over the lifetime.
					radii[i] = info.MaxRadius * d.Scale * d.Progress;

					// Strength fades as the ring expands, so it vanishes as it reaches MaxRadius.
					strengths[i] = info.Strength * d.Scale * (1f - d.Progress);
				}

				shader.SetTexture("WorldTexture", Game.Renderer.WorldBufferSnapshot());

				// ANGLE/ES rejects glUniformXfv with count > 1 on array uniforms, so set each element individually.
				for (var i = 0; i < batchSize; i++)
				{
					shader.SetVec(CentersKeys[i], centers[i * 2], centers[i * 2 + 1]);
					shader.SetVec(RadiiKeys[i], radii[i]);
					shader.SetVec(StrengthsKeys[i], strengths[i]);
				}

				shader.SetVec("DistortionCount", (float)batchSize);
				shader.SetVec("RingThickness", info.RingThickness);
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
