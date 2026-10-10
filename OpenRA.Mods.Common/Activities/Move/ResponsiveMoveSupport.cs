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

using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	interface IResponsiveMoveTarget : IActivityInterface
	{
		bool TryGetResponsiveMoveTarget(Actor self, out WPos targetPosition);
	}

	static class ResponsiveMoveSupport
	{
		public readonly struct ResponsiveLanding
		{
			public readonly CPos Cell;
			public readonly SubCell SubCell;
			public readonly WPos Position;
			public readonly WAngle Facing;

			public ResponsiveLanding(CPos cell, SubCell subCell, WPos position, WAngle facing)
			{
				Cell = cell;
				SubCell = subCell;
				Position = position;
				Facing = facing;
			}
		}

		public static bool TryGetLanding(Actor self, Mobile mobile,
			CPos fromCell, SubCell fromSubCell, CPos toCell, SubCell toSubCell,
			out ResponsiveLanding landing)
		{
			landing = default;
			if (!mobile.Info.ResponsiveBetweenCells)
				return false;

			var currentActivity = self.CurrentActivity;
			if (currentActivity == null || !currentActivity.IsCanceling)
				return false;

			var replacementActivity = currentActivity.NextActivity;
			if (replacementActivity != null)
			{
				mobile.ClearResponsiveStopRequest();
				return TryGetReplacementLanding(self, mobile, replacementActivity,
					fromCell, fromSubCell, toCell, toSubCell,
					out landing);
			}

			if (!mobile.HasResponsiveStopRequest)
				return false;

			var success = TryGetStopLanding(self, mobile, currentActivity,
				fromCell, fromSubCell, toCell, toSubCell,
				out landing);
			if (success)
			{
				ReserveLanding(self, mobile, landing);
				mobile.ClearResponsiveStopRequest();
			}

			return success;
		}

		static bool TryGetStopLanding(Actor self, Mobile mobile, Activity currentActivity,
			CPos fromCell, SubCell fromSubCell, CPos toCell, SubCell toSubCell,
			out ResponsiveLanding landing)
		{
			landing = default;
			if (TryGetActivityTarget(self, currentActivity, out var targetPosition))
				return TryChooseLanding(self, mobile, fromCell, fromSubCell, toCell, toSubCell,
					self.CenterPosition, preferNearest: false, targetPosition, out landing);

			return TryChooseLanding(self, mobile, fromCell, fromSubCell, toCell, toSubCell,
				self.CenterPosition, preferNearest: true, targetPosition: default, out landing);
		}

		static bool TryGetReplacementLanding(Actor self, Mobile mobile, Activity replacementActivity,
			CPos fromCell, SubCell fromSubCell, CPos toCell, SubCell toSubCell,
			out ResponsiveLanding landing)
		{
			landing = default;
			if (!TryGetActivityTarget(self, replacementActivity, out var targetPosition))
				return false;

			var success = TryChooseLanding(self, mobile, fromCell, fromSubCell, toCell, toSubCell,
				self.CenterPosition, preferNearest: false, targetPosition, out landing);
			if (success)
				ReserveLanding(self, mobile, landing);

			return success;
		}

		static bool TryGetActivityTarget(Actor self, Activity activity, out WPos targetPosition)
		{
			if (activity is IResponsiveMoveTarget responsiveTarget && responsiveTarget.TryGetResponsiveMoveTarget(self, out targetPosition))
				return true;

			foreach (var childTarget in activity.ActivitiesImplementing<IResponsiveMoveTarget>())
				if (childTarget.TryGetResponsiveMoveTarget(self, out targetPosition))
					return true;

			foreach (var target in activity.GetTargets(self))
				if (target.Type != TargetType.Invalid)
				{
					targetPosition = target.CenterPosition;
					return true;
				}

			foreach (var node in activity.TargetLineNodes(self))
				if (node.Target.Type != TargetType.Invalid)
				{
					targetPosition = node.Target.CenterPosition;
					return true;
				}

			targetPosition = default;
			return false;
		}

		static bool TryChooseLanding(Actor self, Mobile mobile,
			CPos fromCell, SubCell fromSubCell, CPos toCell, SubCell toSubCell,
			WPos currentPosition, bool preferNearest, WPos targetPosition, out ResponsiveLanding landing)
		{
			var bestLanding = default(ResponsiveLanding);
			var hasCandidate = false;
			var bestPrimary = long.MaxValue;
			var bestSecondary = long.MaxValue;
			var currentTargetDistance = preferNearest
				? long.MaxValue
				: (currentPosition - targetPosition).HorizontalLengthSquared;

			ConsiderCandidate(fromCell, fromSubCell);
			ConsiderCandidate(toCell, toSubCell);
			landing = bestLanding;
			return hasCandidate;

			void ConsiderCandidate(CPos cell, SubCell subCell)
			{
				if (!mobile.CanStayInCell(cell))
					return;

				subCell = ResolveLandingSubCell(self, mobile, cell, subCell);
				if (subCell == SubCell.Invalid)
					return;

				var position = CellCenterPosition(self, cell, subCell);
				var facingDelta = position - currentPosition;
				var facing = facingDelta.HorizontalLengthSquared != 0 ? facingDelta.Yaw : mobile.Facing;
				var targetDistance = (position - targetPosition).HorizontalLengthSquared;
				if (!preferNearest && targetDistance > currentTargetDistance)
					return;

				var primary = preferNearest
					? (position - currentPosition).HorizontalLengthSquared
					: targetDistance;
				var secondary = preferNearest
					? (cell == toCell && subCell == toSubCell ? 0 : 1)
					: (position - currentPosition).HorizontalLengthSquared;

				if (hasCandidate && (primary > bestPrimary || (primary == bestPrimary && secondary >= bestSecondary)))
					return;

				hasCandidate = true;
				bestPrimary = primary;
				bestSecondary = secondary;
				bestLanding = new ResponsiveLanding(cell, subCell, position, facing);
			}
		}

		static SubCell ResolveLandingSubCell(Actor self, Mobile mobile, CPos cell, SubCell preferredSubCell)
		{
			preferredSubCell = mobile.GetValidSubCell(preferredSubCell);

			if (!mobile.Info.LocomotorInfo.SharesCell)
				return mobile.GetAvailableSubCell(cell, preferredSubCell, self);

			return self.World.ActorMap.FreeSubCell(cell, preferredSubCell, a => a != self);
		}

		static void ReserveLanding(Actor self, Mobile mobile, ResponsiveLanding landing)
		{
			mobile.SetLocation(landing.Cell, landing.SubCell, landing.Cell, landing.SubCell);
			mobile.SetCenterPosition(self, self.CenterPosition);
		}

		static WPos CellCenterPosition(Actor self, CPos cell, SubCell subCell)
		{
			var position = cell.Layer == 0 ? self.World.Map.CenterOfCell(cell) :
				self.World.GetCustomMovementLayers()[cell.Layer].CenterOfCell(cell);

			position += self.World.Map.Grid.OffsetOfSubCell(subCell);
			position -= new WVec(0, 0, self.World.Map.DistanceAboveTerrain(position).Length);
			return position;
		}
	}

	sealed class ResponsiveMoveLanding : Activity
	{
		readonly Mobile mobile;
		readonly WPos start;
		readonly ResponsiveMoveSupport.ResponsiveLanding landing;
		readonly CPos landingFromCell;
		readonly SubCell landingFromSubCell;
		readonly CPos landingToCell;
		readonly SubCell landingToSubCell;
		readonly int length;
		int ticks;

		public ResponsiveMoveLanding(Mobile mobile, WPos start, ResponsiveMoveSupport.ResponsiveLanding landing,
			CPos landingFromCell, SubCell landingFromSubCell, CPos landingToCell, SubCell landingToSubCell)
		{
			this.mobile = mobile;
			this.start = start;
			this.landing = landing;
			this.landingFromCell = landingFromCell;
			this.landingFromSubCell = landingFromSubCell;
			this.landingToCell = landingToCell;
			this.landingToSubCell = landingToSubCell;
			var speed = mobile.MovementSpeedForCell(landing.Cell);
			length = speed > 0 ? (landing.Position - start).Length / speed : 0;
			IsInterruptible = false;
		}

		public override bool Tick(Actor self)
		{
			if (ResponsiveMoveSupport.TryGetLanding(self, mobile,
				landingFromCell, landingFromSubCell, landingToCell, landingToSubCell,
				out var nextLanding)
				&& (nextLanding.Cell != landing.Cell || nextLanding.SubCell != landing.SubCell))
			{
				Queue(new ResponsiveMoveLanding(mobile, mobile.CenterPosition, nextLanding,
					landingFromCell, landingFromSubCell, landingToCell, landingToSubCell));
				return true;
			}

			var pos = length > 1
				? WPos.Lerp(start, landing.Position, ticks, length - 1)
				: landing.Position;

			mobile.SetCenterPosition(self, pos);
			mobile.Facing = Util.TickFacing(mobile.Facing, landing.Facing, mobile.TurnSpeed);

			if (++ticks >= length)
			{
				mobile.SetPosition(self, landing.Cell, landing.SubCell);
				mobile.Facing = landing.Facing;
				return true;
			}

			return false;
		}
	}
}
