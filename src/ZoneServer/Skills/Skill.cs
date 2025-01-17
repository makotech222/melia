﻿using System;
using System.Threading;
using Melia.Shared.Data.Database;
using Melia.Shared.ObjectProperties;
using Melia.Shared.Tos.Const;
using Melia.Shared.World;
using Melia.Zone.Network;
using Melia.Zone.Skills.SplashAreas;
using Melia.Zone.World.Actors;
using Melia.Zone.World.Actors.Characters;
using Melia.Zone.World.Actors.CombatEntities.Components;
using Melia.Zone.World.Actors.Monsters;
using Yggdrasil.Scheduling;
using Yggdrasil.Util;

namespace Melia.Zone.Skills
{
	public class Skill : IPropertyObject, IUpdateable
	{
		private static long ObjectIds = ObjectIdRanges.Skills;

		/// <summary>
		/// The skill's unique id.
		/// </summary>
		/// <remarks>
		/// We're basically assigning a new object id to every skill here,
		/// every time one is created. That *might* work, but we have to be
		/// careful. If there's a single case where the skill id needs to
		/// persist, we have to seriously rethink our approach to object
		/// ids, and potentially create a global pool on the database or
		/// something.
		/// </remarks>
		public long ObjectId { get; } = Interlocked.Increment(ref ObjectIds);

		/// <summary>
		/// Returns reference to the skill's properties.
		/// </summary>
		public SkillProperties Properties { get; }

		/// <summary>
		/// Returns reference to the skill's properties.
		/// </summary>
		Properties IPropertyHolder.Properties => this.Properties;

		/// <summary>
		/// Returns reference to the skill's owner.
		/// </summary>
		public ICombatEntity Owner { get; }

		/// <summary>
		/// Returns the skill's class id.
		/// </summary>
		public SkillId Id { get; }

		/// <summary>
		/// Gets or sets skill's level.
		/// </summary>
		public int Level { get; set; }

		/// <summary>
		/// Returns the amount of SP necessary to use the skill.
		/// </summary>
		public int SpendSp
			=> (int)this.Properties.GetFloat(PropertyName.SpendSP);

		/// <summary>
		/// Returns the skill's overheat count. If this value reaches the
		/// skill's maximum overheat, the skill goes on a cooldown.
		/// </summary>
		public int OverheatCounter { get; private set; }

		/// <summary>
		/// Returns the time until the skill's overheat counter is reset.
		/// </summary>
		public TimeSpan OverheatTimeRemaining { get; private set; }

		/// <summary>
		/// Returns reference to the skill's data from the file database.
		/// </summary>
		public SkillData Data { get; }

		/// <summary>
		/// Returns reference to the skill's cooldow ndata from the file
		/// database.
		/// </summary>
		public CooldownData CooldownData { get; }

		/// <summary>
		/// Returns reference to the skill's overheat data from the file
		/// database.
		/// </summary>
		public CooldownData OverheatData { get; }

		/// <summary>
		/// Returns the skill's temporary variables.
		/// </summary>
		public Variables Vars { get; } = new Variables();

		/// <summary>
		/// Returns whether this skill can overheat.
		/// </summary>
		public bool CanOverheat => this.Data.OverheatCount > 0;

		/// <summary>
		/// Returns whether the skill is currently overheated.
		/// </summary>
		public bool IsOverheated => this.CanOverheat && this.OverheatCounter >= this.Data.OverheatCount;

		/// <summary>
		/// Returns true if the skill is currently on cooldown.
		/// </summary>
		public bool IsOnCooldown => this.Owner.Components.Get<CooldownComponent>().IsOnCooldown(this.Data.CooldownGroup);

		/// <summary>
		/// Creates a new instance.
		/// </summary>
		/// <param name="owner"></param>
		/// <param name="skillId"></param>
		/// <param name="level"></param>
		public Skill(ICombatEntity owner, SkillId skillId, int level)
		{
			this.Owner = owner;
			this.Id = skillId;
			this.Level = level;

			this.Data = ZoneServer.Instance.Data.SkillDb.Find(skillId) ?? throw new ArgumentException($"Unknown skill '{skillId}'.");
			this.CooldownData = ZoneServer.Instance.Data.CooldownDb.Find(this.Data.CooldownGroup) ?? throw new ArgumentException($"Unknown skill '{skillId}' cooldown group '{this.Data.CooldownGroup}'.");
			this.OverheatData = ZoneServer.Instance.Data.CooldownDb.Find(this.Data.OverheatGroup) ?? throw new ArgumentException($"Unknown skill '{skillId}' overheat group '{this.Data.OverheatGroup}'.");

			this.Properties = new SkillProperties(this);
		}

		/// <summary>
		/// Increases skill's overheat counter by 1 if the skill can
		/// overheat and updates the client.
		/// </summary>
		public void IncreaseOverheat()
		{
			// No cooldowns for monsters
			if (!(this.Owner is Character character))
				return;

			// Increase counter regardless of whether the skill can
			// overheat. In both cases we will eventually get over
			// the overheat counter, in which case we reset the
			// overheat and go on cooldown.
			this.OverheatCounter++;
			this.OverheatTimeRemaining = this.OverheatData.OverheatResetTime;

			if (this.OverheatCounter >= this.Data.OverheatCount)
			{
				this.OverheatCounter = 0;
				this.OverheatTimeRemaining = TimeSpan.Zero;

				this.Owner.Components.Get<CooldownComponent>().Start(this.Data.CooldownGroup, this.Data.CooldownTime);
			}

			// Update the overheat after the max was checked so we reset it
			// to 0 if we went into cooldown
			Send.ZC_OVERHEAT_CHANGED(character, this);
		}

		/// <summary>
		/// Generates a splash area based on the given arguments and the
		/// skill's data.
		/// </summary>
		/// <param name="originPos"></param>
		/// <param name="dir"></param>
		/// <returns></returns>
		public ISplashArea GetSplashArea(Position originPos, Direction dir)
		{
			var skill = this;
			ISplashArea splashArea;

			switch (this.Data.SplashType)
			{
				default:
				case SplashType.Square:
				{
					// These sizes and which properties or what data to
					// use is currently all guess-work. There must be a
					// consistent way to calculate the splash area though,
					// as I can't imagine that every single monster skill
					// has that value defined manually.

					var height = skill.Data.SplashHeight;
					var width = skill.Data.SplashRange;

					splashArea = new Square(originPos, dir, height, width);
					break;
				}
				case SplashType.Circle:
				{
					var radius = skill.Data.SplashHeight;

					splashArea = new Circle(originPos, radius);
					break;
				}
				case SplashType.Fan:
				{
					var height = skill.Data.SplashHeight;
					var angle = skill.Data.SplashAngle;

					splashArea = new Fan(originPos, dir, height, angle);
					break;
				}
			}

			return splashArea;
		}

		/// <summary>
		/// Returns the minimum range to the target within which the skill
		/// can be used.
		/// </summary>
		/// <returns></returns>
		public float GetAttackRange()
		{
			// Guessed, see GetSplashArea. Take a little off the top,
			// so entities actually have to get into the splash area.
			return this.Data.SplashHeight;
		}

		/// <summary>
		/// Updates the skill's overheat.
		/// </summary>
		/// <param name="elapsed"></param>
		public void Update(TimeSpan elapsed)
		{
			if (this.OverheatTimeRemaining > TimeSpan.Zero)
			{
				this.OverheatTimeRemaining = Math2.Max(TimeSpan.Zero, this.OverheatTimeRemaining - elapsed);

				// There's no need to update the client as it resets the
				// overheat counter automatically after the overheat reset
				// time passed
				if (this.OverheatTimeRemaining == TimeSpan.Zero)
					this.OverheatCounter = 0;
			}
		}
	}
}
