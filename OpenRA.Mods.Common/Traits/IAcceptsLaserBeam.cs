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

using OpenRA.Primitives;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>
	/// Implemented by world-actor traits that want to receive LaserZap beam geometry
	/// each render frame (e.g. to drive a screen-space glow effect).
	/// </summary>
	public interface IAcceptsLaserBeam
	{
		void RegisterBeam(WPos source, WPos target, Color color);
	}
}
