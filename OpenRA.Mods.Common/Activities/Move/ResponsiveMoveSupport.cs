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
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	internal enum ResponsiveCancelType
	{
		ReplacementActivity,
		LandBeforeNextActivity
	}

	internal readonly struct ResponsiveLanding
	{
		public readonly CPos Cell;
		public readonly SubCell SubCell;
		public readonly WPos Position;

		public ResponsiveLanding(CPos cell, SubCell subCell, WPos position)
		{
			Cell = cell;
			SubCell = subCell;
			Position = position;
		}
	}

	readonly struct ResponsiveCancelRequest
	{
		public readonly bool HasReplacementActivity;
		public readonly WPos? PreferredLandingPosition;

		public ResponsiveCancelRequest(bool hasReplacementActivity, WPos? preferredLandingPosition)
		{
			HasReplacementActivity = hasReplacementActivity;
			PreferredLandingPosition = preferredLandingPosition;
		}
	}

	internal readonly struct ResponsiveCancelResolution
	{
		public readonly ResponsiveLanding Landing;
		public readonly bool HasReplacementActivity;

		public ResponsiveCancelResolution(ResponsiveLanding landing, bool hasReplacementActivity)
		{
			Landing = landing;
			HasReplacementActivity = hasReplacementActivity;
		}
	}

	internal sealed class ResponsiveMoveSupport
	{
		readonly Mobile mobile;
		bool hasReplacementActivityOnCancel;
		bool forceLandingBeforeNextActivity;
		WPos? preferredLandingPosition;

		public ResponsiveMoveSupport(Mobile mobile)
		{
			this.mobile = mobile;
		}

		public void Reset()
		{
			hasReplacementActivityOnCancel = false;
			forceLandingBeforeNextActivity = false;
			preferredLandingPosition = null;
		}

		public void NotifyCancel(ResponsiveCancelType? type = null, WPos? preferredLandingPosition = null)
		{
			if (type == ResponsiveCancelType.ReplacementActivity)
				hasReplacementActivityOnCancel = true;
			else if (type == ResponsiveCancelType.LandBeforeNextActivity)
				forceLandingBeforeNextActivity = true;

			if (preferredLandingPosition.HasValue)
				this.preferredLandingPosition = preferredLandingPosition;
		}

		public bool TryResolveCancel(Actor self, bool isCanceling, bool hasNextActivity, out ResponsiveCancelResolution resolution)
		{
			resolution = default;
			if (!isCanceling || !mobile.Info.ResponsiveBetweenCells)
				return false;

			var request = ConsumeRequest(hasNextActivity);
			var currentPosition = mobile.CenterPosition;
			var hasFromLanding = TryGetLanding(self, mobile.FromCell, mobile.FromSubCell, request, out var fromLanding);
			var hasToLanding = TryGetLanding(self, mobile.ToCell, mobile.ToSubCell, request, out var toLanding);

			if (!mobile.IsMovingBetweenCells)
				hasFromLanding = false;

			if (!hasFromLanding && !hasToLanding)
				return false;

			if (!hasFromLanding)
			{
				resolution = new ResponsiveCancelResolution(toLanding, request.HasReplacementActivity);
				return true;
			}

			if (!hasToLanding)
			{
				resolution = new ResponsiveCancelResolution(fromLanding, request.HasReplacementActivity);
				return true;
			}

			var useToLanding = request.PreferredLandingPosition.HasValue
				? ChooseLandingEndpoint(currentPosition, request.PreferredLandingPosition.Value, fromLanding.Position, toLanding.Position)
				: (toLanding.Position - currentPosition).LengthSquared <= (fromLanding.Position - currentPosition).LengthSquared;

			resolution = new ResponsiveCancelResolution(useToLanding ? toLanding : fromLanding, request.HasReplacementActivity);
			return true;
		}

		public bool ShouldStartNextSegmentFromCurrentPosition(Actor self)
		{
			return TryGetResolvedSingleCellLanding(self, out _);
		}

		public bool TryGetSettleLanding(Actor self, out ResponsiveLanding landing)
		{
			if (!TryGetResolvedSingleCellLanding(self, out landing))
				return false;

			if ((landing.Position - mobile.CenterPosition).LengthSquared == 0)
				return false;

			return true;
		}

		bool TryGetResolvedSingleCellLanding(Actor self, out ResponsiveLanding landing)
		{
			landing = default;
			if (!mobile.Info.ResponsiveBetweenCells || mobile.FromCell != mobile.ToCell)
				return false;

			landing = new ResponsiveLanding(mobile.ToCell, mobile.ToSubCell, CellCenterPosition(self, mobile.ToCell, mobile.ToSubCell));
			return true;
		}

		ResponsiveCancelRequest ConsumeRequest(bool hasNextActivity)
		{
			var request = new ResponsiveCancelRequest(
				!forceLandingBeforeNextActivity && (hasNextActivity || hasReplacementActivityOnCancel),
				preferredLandingPosition);

			hasReplacementActivityOnCancel = false;
			forceLandingBeforeNextActivity = false;
			preferredLandingPosition = null;

			return request;
		}

		bool TryGetLanding(Actor self, CPos cell, SubCell preferredSubCell, in ResponsiveCancelRequest request, out ResponsiveLanding landing)
		{
			landing = default;
			if (!mobile.CanStayInCell(cell))
				return false;

			var subCell = ChooseLandingSubCell(self, cell, preferredSubCell, request);
			if (subCell == SubCell.Invalid)
				return false;

			landing = new ResponsiveLanding(cell, subCell, CellCenterPosition(self, cell, subCell));
			return true;
		}

		SubCell ChooseLandingSubCell(Actor self, CPos cell, SubCell preferredSubCell, in ResponsiveCancelRequest request)
		{
			if (!mobile.Info.LocomotorInfo.SharesCell)
				return SubCell.FullCell;

			var blockedBy = request.HasReplacementActivity ? BlockedByActor.Stationary : BlockedByActor.All;

			if (!request.PreferredLandingPosition.HasValue)
				return mobile.GetAvailableSubCell(cell, preferredSubCell, self, blockedBy);

			var currentPosition = mobile.CenterPosition;
			var currentPreferenceDistance = (currentPosition - request.PreferredLandingPosition.Value).LengthSquared;
			var bestSubCell = SubCell.Invalid;
			var bestImprovingSubCell = SubCell.Invalid;
			var bestDistance = long.MaxValue;
			var bestImprovingDistance = long.MaxValue;

			for (var i = 1; i < self.World.Map.Grid.SubCellOffsets.Length; i++)
			{
				var candidate = (SubCell)i;
				if (mobile.GetAvailableSubCell(cell, candidate, self, blockedBy) != candidate)
					continue;

				var candidatePosition = CellCenterPosition(self, cell, candidate);
				var distance = (candidatePosition - request.PreferredLandingPosition.Value).LengthSquared;
				var movementDistance = (candidatePosition - currentPosition).LengthSquared;
				if (distance <= currentPreferenceDistance && movementDistance < bestImprovingDistance)
				{
					bestImprovingDistance = movementDistance;
					bestImprovingSubCell = candidate;
				}

				if (distance < bestDistance)
				{
					bestDistance = distance;
					bestSubCell = candidate;
				}
			}

			if (bestImprovingSubCell != SubCell.Invalid)
				return bestImprovingSubCell;

			return bestSubCell != SubCell.Invalid ? bestSubCell : mobile.GetAvailableSubCell(cell, preferredSubCell, self, blockedBy);
		}

		static bool ChooseLandingEndpoint(WPos currentPosition, WPos preferredPosition, WPos fromPosition, WPos toPosition)
		{
			var currentPreferenceDistance = (currentPosition - preferredPosition).LengthSquared;
			var fromPreferenceDistance = (fromPosition - preferredPosition).LengthSquared;
			var toPreferenceDistance = (toPosition - preferredPosition).LengthSquared;
			var fromImproves = fromPreferenceDistance <= currentPreferenceDistance;
			var toImproves = toPreferenceDistance <= currentPreferenceDistance;

			if (fromImproves != toImproves)
				return toImproves;

			if (fromImproves && toImproves)
				return (toPosition - currentPosition).LengthSquared <= (fromPosition - currentPosition).LengthSquared;

			return toPreferenceDistance <= fromPreferenceDistance;
		}

		public static WPos CellCenterPosition(Actor self, CPos cell, SubCell subCell)
		{
			var position = cell.Layer == 0 ? self.World.Map.CenterOfCell(cell) :
				self.World.GetCustomMovementLayers()[cell.Layer].CenterOfCell(cell);

			position += self.World.Map.Grid.OffsetOfSubCell(subCell);
			position -= new WVec(0, 0, self.World.Map.DistanceAboveTerrain(position).Length);
			return position;
		}
	}

	internal static class ResponsiveMoveForwarder
	{
		public static void Notify(Activity activity, ResponsiveCancelType? type = null, WPos? preferredLandingPosition = null)
		{
			if (activity == null)
				return;

			foreach (var move in activity.ActivitiesImplementing<Move>())
			{
				move.NotifyResponsiveCancel(type, preferredLandingPosition);
				break;
			}
		}
	}
}
