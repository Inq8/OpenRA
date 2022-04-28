#region Copyright & License Information
/*
 * Copyright 2007-2022 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Linq;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	[Desc("Replaces the unit animation when capturing.")]
	public class WithCaptureAnimationInfo : ConditionalTraitInfo, Requires<WithSpriteBodyInfo>
	{
		[SequenceReference]
		[Desc("Sequence name to use")]
		public readonly string Sequence = "capture";

		[Desc("Which sprite body to play the animation on.")]
		public readonly string Body = "body";

		public override object Create(ActorInitializer init) { return new WithCaptureAnimation(init.Self, this); }
	}

	public class WithCaptureAnimation : ConditionalTrait<WithCaptureAnimationInfo>, INotifyCapture
	{
		readonly WithSpriteBody wsb;

		public WithCaptureAnimation(Actor self, WithCaptureAnimationInfo info)
			: base(info)
		{
			wsb = self.TraitsImplementing<WithSpriteBody>().Single(w => w.Info.Name == Info.Body);
		}

		void INotifyCapture.OnCapture(Actor self, Actor captor, Player oldOwner, Player newOwner, BitSet<CaptureType> captureTypes)
		{
			if (!IsTraitDisabled)
				wsb.PlayCustomAnimation(self, Info.Sequence, () => wsb.CancelCustomAnimation(self));
		}

		protected override void TraitDisabled(Actor self)
		{
			wsb.CancelCustomAnimation(self);
		}
	}
}
