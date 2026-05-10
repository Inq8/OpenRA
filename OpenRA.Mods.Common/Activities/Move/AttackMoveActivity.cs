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
using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public class AttackMoveActivity : Activity
	{
		readonly Func<Activity> getMove;
		readonly bool isAssaultMove;
		readonly AutoTarget autoTarget;
		readonly AttackMove attackMove;

		bool runningMoveActivity = false;
		int token = Actor.InvalidConditionToken;
		Target target = Target.Invalid;

		public AttackMoveActivity(Actor self, Func<Activity> getMove, bool assaultMoving = false)
		{
			this.getMove = getMove;
			autoTarget = self.TraitOrDefault<AutoTarget>();
			attackMove = self.TraitOrDefault<AttackMove>();
			isAssaultMove = assaultMoving;
			ChildHasPriority = false;
		}

		protected override void OnFirstRun(Actor self)
		{
			if (attackMove == null || autoTarget == null)
			{
				QueueChild(getMove());
				return;
			}

			if (isAssaultMove)
				token = self.GrantCondition(attackMove.Info.AssaultMoveCondition);
			else
				token = self.GrantCondition(attackMove.Info.AttackMoveCondition);
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling || attackMove == null || autoTarget == null)
			{
				// Immediate attack-move replacements are queued on this wrapper activity.
				// Forward that information to the child Move so ResponsiveBetweenCells can treat
				// the cancel as a redirect instead of a stop-style nearest-cell landing.
				if (IsCanceling && NextActivity != null)
					ResponsiveMoveForwarder.Notify(ChildActivity, ResponsiveCancelType.ReplacementActivity);

				return TickChild(self);
			}

			// We are currently not attacking, so scan for new targets.
			if (ChildActivity == null || runningMoveActivity)
			{
				// Use the standard ScanForTarget rate limit while we are running the move activity to save performance.
				// Override the rate limit if our attack activity has completed so we can immediately acquire a new target instead of moving.
				target = autoTarget.ScanForTarget(self, autoTarget.AllowMove, true, !runningMoveActivity);

				// Cancel the current move activity and queue attack activities if we find a new target.
				if (target.Type != TargetType.Invalid)
				{
					runningMoveActivity = false;

					// Let ResponsiveBetweenCells finish resolving the current move before
					// starting the attack activity. Queuing the attack directly onto the
					// current move would make Move treat it as an in-flight redirect.
					if (ChildActivity != null)
					{
						ResponsiveMoveForwarder.Notify(ChildActivity, preferredLandingPosition: target.CenterPosition);
						ChildActivity.Cancel(self);
					}
					else
						foreach (var ab in autoTarget.ActiveAttackBases)
							QueueChild(ab.GetAttackActivity(self, AttackSource.AttackMove, target, autoTarget.AllowMove, false));
				}

				// Continue with the move activity (or queue a new one) when there are no targets.
				if (ChildActivity == null)
				{
					runningMoveActivity = true;
					QueueChild(getMove());
				}
			}

			// If the move activity finished, we have reached our destination and there are no more enemies on our path.
			return TickChild(self) && runningMoveActivity;
		}

		protected override void OnLastRun(Actor self)
		{
			if (token != Actor.InvalidConditionToken)
				token = self.RevokeCondition(token);
		}

		public override IEnumerable<Target> GetTargets(Actor self)
		{
			if (ChildActivity != null)
				return ChildActivity.GetTargets(self);

			return Target.None;
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			foreach (var n in getMove().TargetLineNodes(self))
				yield return n;
		}
	}
}
