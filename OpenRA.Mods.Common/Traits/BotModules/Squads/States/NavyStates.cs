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

using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.BotModules.Squads
{
	abstract class NavyStateBase : StateBase
	{
		protected virtual bool ShouldFlee(Squad owner)
		{
			return ShouldFlee(owner, enemies => !AttackOrFleeFuzzy.Default.CanAttack(owner.Units, enemies));
		}

		protected static Actor FindClosestEnemy(Squad owner)
		{
			var first = owner.Units.First();

			// Navy squad AI can exploit enemy naval production to find path, if any.
			// (Way better than finding a nearest target which is likely to be on Ground)
			// You might be tempted to move these lookups into Activate() but that causes null reference exception.
			var mobile = first.Trait<Mobile>();

			var navalProductions = owner.World.ActorsHavingTrait<Building>().Where(a
				=> owner.SquadManager.Info.NavalProductionTypes.Contains(a.Info.Name)
				&& mobile.PathFinder.PathExistsForLocomotor(mobile.Locomotor, first.Location, a.Location)
				&& a.AppearsHostileTo(first));

			var nearest = navalProductions.ClosestTo(first);
			if (nearest != null)
			{
				// Return nearest when it is FAR enough.
				// If the naval production is within MaxBaseRadius, it implies that
				// this squad is close to enemy territory and they should expect a naval combat;
				// closest enemy makes more sense in that case.
				if ((nearest.Location - first.Location).LengthSquared > owner.SquadManager.Info.MaxBaseRadius * owner.SquadManager.Info.MaxBaseRadius)
					return nearest;
			}

			return owner.SquadManager.FindClosestEnemy(first.CenterPosition);
		}
	}

	sealed class NavyUnitsIdleState : NavyStateBase, IState
	{
		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			if (!owner.IsTargetValid)
			{
				var closestEnemy = FindClosestEnemy(owner);
				if (closestEnemy == null)
					return;

				owner.TargetActor = closestEnemy;
			}

			var enemyUnits = owner.World.FindActorsInCircle(owner.TargetActor.CenterPosition, WDist.FromCells(owner.SquadManager.Info.IdleScanRadius))
				.Where(owner.SquadManager.IsPreferredEnemyUnit).ToList();

			if (enemyUnits.Count == 0)
				return;

			if (AttackOrFleeFuzzy.Default.CanAttack(owner.Units, enemyUnits))
			{
				owner.Bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(owner.World, owner.TargetActor.Location), false, groupedActors: owner.Units.ToArray()));

				// We have gathered sufficient units. Attack the nearest enemy unit.
				owner.FuzzyStateMachine.ChangeState(owner, new NavyUnitsAttackMoveState(), true);
			}
			else
				owner.FuzzyStateMachine.ChangeState(owner, new NavyUnitsFleeState(), true);
		}

		public void Deactivate(Squad owner) { }
	}

	sealed class NavyUnitsAttackMoveState : NavyStateBase, IState
	{
		int lastUpdatedTick;
		CPos? lastLeaderLocation;
		Actor lastTarget;

		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			if (!owner.IsTargetValid)
			{
				var closestEnemy = FindClosestEnemy(owner);
				if (closestEnemy != null)
					owner.TargetActor = closestEnemy;
				else
				{
					owner.FuzzyStateMachine.ChangeState(owner, new NavyUnitsFleeState(), true);
					return;
				}
			}

			var leader = owner.Units.ClosestTo(owner.TargetActor.CenterPosition);
			if (leader == null)
				return;

			if (leader.Location != lastLeaderLocation)
			{
				lastLeaderLocation = leader.Location;
				lastUpdatedTick = owner.World.WorldTick;
			}

			if (owner.TargetActor != lastTarget)
			{
				lastTarget = owner.TargetActor;
				lastUpdatedTick = owner.World.WorldTick;
			}

			// HACK: Drop back to the idle state if we haven't moved in 2.5 seconds
			// This works around the squad being stuck trying to attack-move to a location
			// that they cannot path to, generating expensive pathfinding calls each tick.
			if (owner.World.WorldTick > lastUpdatedTick + 63)
			{
				owner.FuzzyStateMachine.ChangeState(owner, new NavyUnitsIdleState(), true);
				return;
			}

			var ownUnits = owner.World.FindActorsInCircle(leader.CenterPosition, WDist.FromCells(owner.Units.Count) / 3)
				.Where(a => a.Owner == owner.Units.First().Owner && owner.Units.Contains(a)).ToHashSet();

			if (ownUnits.Count < owner.Units.Count)
			{
				// Since units have different movement speeds, they get separated while approaching the target.
				// Let them regroup into tighter formation.
				owner.Bot.QueueOrder(new Order("Stop", leader, false));

				var units = owner.Units.Where(a => !ownUnits.Contains(a)).ToArray();
				owner.Bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(owner.World, leader.Location), false, groupedActors: units));
			}
			else
			{
				var enemies = owner.World.FindActorsInCircle(leader.CenterPosition, WDist.FromCells(owner.SquadManager.Info.AttackScanRadius))
					.Where(owner.SquadManager.IsPreferredEnemyUnit);
				var target = enemies.ClosestTo(leader.CenterPosition);
				if (target != null)
				{
					owner.TargetActor = target;
					owner.FuzzyStateMachine.ChangeState(owner, new NavyUnitsAttackState(), true);
				}
				else
					owner.Bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(owner.World, owner.TargetActor.Location), false, groupedActors: owner.Units.ToArray()));
			}

			if (ShouldFlee(owner))
				owner.FuzzyStateMachine.ChangeState(owner, new NavyUnitsFleeState(), true);
		}

		public void Deactivate(Squad owner) { }
	}

	sealed class NavyUnitsAttackState : NavyStateBase, IState
	{
		int lastUpdatedTick;
		CPos? lastLeaderLocation;
		Actor lastTarget;

		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			if (!owner.IsTargetValid)
			{
				var closestEnemy = FindClosestEnemy(owner);
				if (closestEnemy != null)
					owner.TargetActor = closestEnemy;
				else
				{
					owner.FuzzyStateMachine.ChangeState(owner, new NavyUnitsFleeState(), true);
					return;
				}
			}

			var leader = owner.Units.ClosestTo(owner.TargetActor.CenterPosition);
			if (leader.Location != lastLeaderLocation)
			{
				lastLeaderLocation = leader.Location;
				lastUpdatedTick = owner.World.WorldTick;
			}

			if (owner.TargetActor != lastTarget)
			{
				lastTarget = owner.TargetActor;
				lastUpdatedTick = owner.World.WorldTick;
			}

			// HACK: Drop back to the idle state if we haven't moved in 2.5 seconds
			// This works around the squad being stuck trying to attack-move to a location
			// that they cannot path to, generating expensive pathfinding calls each tick.
			if (owner.World.WorldTick > lastUpdatedTick + 63)
			{
				owner.FuzzyStateMachine.ChangeState(owner, new NavyUnitsIdleState(), true);
				return;
			}

			foreach (var a in owner.Units)
				if (!BusyAttack(a))
					owner.Bot.QueueOrder(new Order("Attack", a, Target.FromActor(owner.TargetActor), false));

			if (ShouldFlee(owner))
				owner.FuzzyStateMachine.ChangeState(owner, new NavyUnitsFleeState(), true);
		}

		public void Deactivate(Squad owner) { }
	}

	sealed class NavyUnitsFleeState : NavyStateBase, IState
	{
		public void Activate(Squad owner) { }

		public void Tick(Squad owner)
		{
			if (!owner.IsValid)
				return;

			GoToRandomOwnBuilding(owner);
			owner.FuzzyStateMachine.ChangeState(owner, new NavyUnitsIdleState(), true);
		}

		public void Deactivate(Squad owner) { owner.Units.Clear(); }
	}
}
