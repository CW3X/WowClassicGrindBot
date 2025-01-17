using Core.GOAP;
using Microsoft.Extensions.Logging;
using SharedLib.NpcFinder;
using System;
using System.Linq;

namespace Core.Goals
{
    public class PullTargetGoal : GoapGoal, IGoapEventListener
    {
        public override float Cost => 7f;

        private readonly ILogger logger;
        private readonly ConfigurableInput input;
        private readonly Wait wait;
        private readonly AddonReader addonReader;
        private readonly PlayerReader playerReader;
        private readonly StopMoving stopMoving;
        private readonly StuckDetector stuckDetector;
        private readonly NpcNameTargeting npcNameTargeting;
        private readonly CastingHandler castingHandler;
        private readonly MountHandler mountHandler;
        private readonly CombatUtil combatUtil;
        private readonly IBlacklist blacklist;

        private readonly Random random = new();

        private readonly KeyAction? approachKey;
        private readonly Action approachAction;

        private readonly bool requiresNpcNameFinder;

        private DateTime pullStart;

        private double PullDurationMs => (DateTime.UtcNow - pullStart).TotalMilliseconds;

        public PullTargetGoal(ILogger logger, ConfigurableInput input, Wait wait, AddonReader addonReader, IBlacklist blacklist, StopMoving stopMoving, CastingHandler castingHandler, MountHandler mountHandler, NpcNameTargeting npcNameTargeting, StuckDetector stuckDetector, CombatUtil combatUtil)
            : base(nameof(PullTargetGoal))
        {
            this.logger = logger;
            this.input = input;
            this.wait = wait;
            this.addonReader = addonReader;
            this.playerReader = addonReader.PlayerReader;
            this.stopMoving = stopMoving;
            this.castingHandler = castingHandler;
            this.mountHandler = mountHandler;
            this.npcNameTargeting = npcNameTargeting;
            this.stuckDetector = stuckDetector;
            this.combatUtil = combatUtil;
            this.blacklist = blacklist;

            Keys = input.ClassConfig.Pull.Sequence;

            approachKey = Keys.FirstOrDefault(x => x.Name == input.ClassConfig.Approach.Name);
            approachAction = approachKey == null ? DefaultApproach : ConditionalApproach;

            for (int i = 0; i < Keys.Length; i++)
            {
                if (Keys[i].Requirements.Contains(RequirementFactory.AddVisible))
                {
                    requiresNpcNameFinder = true;
                    break;
                }
            }

            AddPrecondition(GoapKey.incombat, false);
            AddPrecondition(GoapKey.hastarget, true);
            AddPrecondition(GoapKey.targetisalive, true);
            AddPrecondition(GoapKey.targethostile, true);
            AddPrecondition(GoapKey.pulled, false);
            AddPrecondition(GoapKey.withinpullrange, true);

            AddEffect(GoapKey.pulled, true);
        }

        public override void OnEnter()
        {
            combatUtil.Update();

            if (mountHandler.IsMounted())
            {
                mountHandler.Dismount();
            }

            if (Keys.Length != 0 && input.ClassConfig.StopAttack.GetCooldownRemaining() == 0)
            {
                Log("Stop auto interact!");
                input.StopAttack();
                wait.Update();
            }

            if (requiresNpcNameFinder)
            {
                npcNameTargeting.ChangeNpcType(NpcNames.Enemy);
            }

            pullStart = DateTime.UtcNow;
        }

        public override void OnExit()
        {
            if (requiresNpcNameFinder)
            {
                npcNameTargeting.ChangeNpcType(NpcNames.None);
            }
        }

        public void OnGoapEvent(GoapEventArgs e)
        {
            if (e is ResumeEvent)
            {
                pullStart = DateTime.UtcNow;
            }
        }

        public override void Update()
        {
            combatUtil.Update();
            wait.Update();

            if (PullDurationMs > 15_000)
            {
                input.ClearTarget();
                Log("Pull taking too long. Clear target and face away!");
                input.KeyPress(random.Next(2) == 0 ? input.TurnLeftKey : input.TurnRightKey, 1000);

                return;
            }

            if (!Pull())
            {
                if (combatUtil.EnteredCombat() && HasPickedUpAnAdd)
                {
                    Log($"Add on pull! Combat={playerReader.Bits.PlayerInCombat()}, Targets me={playerReader.Bits.TargetOfTargetIsPlayerOrPet()}");

                    stopMoving.Stop();
                    if (combatUtil.AquiredTarget(5000))
                    {
                        pullStart = DateTime.UtcNow;
                    }

                    return;
                }

                approachAction();
            }
            else
            {
                SendGoapEvent(new GoapStateEvent(GoapKey.pulled, true));
            }
        }

        protected bool HasPickedUpAnAdd =>
            playerReader.Bits.PlayerInCombat() &&
            !playerReader.Bits.TargetInCombat();

        protected void WaitForWithinMeleeRange(KeyAction item, bool lastCastSuccess)
        {
            stopMoving.Stop();
            wait.Update();

            var start = DateTime.UtcNow;
            var lastKnownHealth = playerReader.HealthCurrent();
            double maxWaitTimeMs = 10_000;

            Log($"Waiting for the target to reach melee range - max {maxWaitTimeMs}ms");

            while (playerReader.Bits.HasTarget() && !playerReader.IsInMeleeRange() && (DateTime.UtcNow - start).TotalMilliseconds < maxWaitTimeMs)
            {
                if (playerReader.HealthCurrent() < lastKnownHealth)
                {
                    Log("Got damage. Stop waiting for melee range.");
                    break;
                }

                if (playerReader.IsTargetCasting())
                {
                    Log("Target started casting. Stop waiting for melee range.");
                    break;
                }

                if (lastCastSuccess && addonReader.UsableAction.Is(item))
                {
                    Log($"While waiting, repeat current action: {item.Name}");
                    lastCastSuccess = castingHandler.CastIfReady(item, playerReader.IsInMeleeRange);
                    Log($"Repeat current action: {lastCastSuccess}");
                }

                wait.Update();
            }
        }

        public bool Pull()
        {
            if (playerReader.Bits.HasPet() && !playerReader.PetHasTarget)
            {
                if (input.ClassConfig.PetAttack.GetCooldownRemaining() == 0)
                    input.PetAttack();
            }

            bool castAny = false;
            for (int i = 0; i < Keys.Length; i++)
            {
                if (Keys[i].Name == input.ClassConfig.Approach.Name)
                    continue;

                if (!Keys[i].CanRun())
                {
                    continue;
                }

                bool success = castingHandler.Cast(Keys[i], PullPrevention);
                if (success)
                {
                    if (!playerReader.Bits.HasTarget())
                    {
                        return false;
                    }

                    castAny = true;

                    if (Keys[i].WaitForWithinMeleeRange)
                    {
                        WaitForWithinMeleeRange(Keys[i], success);
                    }
                }
                else if (PullPrevention() &&
                    (playerReader.IsCasting() ||
                     playerReader.Bits.SpellOn_AutoAttack() ||
                     playerReader.Bits.SpellOn_AutoShot() ||
                     playerReader.Bits.SpellOn_Shoot()))
                {
                    Log("Preventing pulling possible tagged target!");
                    input.StopAttack();
                    input.ClearTarget();
                    wait.Update();
                    return false;
                }
            }

            if (castAny)
            {
                (bool timeout, double elapsedMs) = wait.Until(1000, SuccessfullPull);
                if (!timeout)
                {
                    Log($"Entered combat after {elapsedMs}ms");
                }
            }

            return playerReader.Bits.PlayerInCombat();
        }

        private void DefaultApproach()
        {
            if (!stuckDetector.IsMoving())
            {
                stuckDetector.Update();
            }

            if (input.ClassConfig.Approach.GetCooldownRemaining() == 0)
            {
                input.Approach();
            }
        }

        private void ConditionalApproach()
        {
            if (approachKey != null && (approachKey.CanRun() || approachKey.GetCooldownRemaining() > 0))
            {
                if (approachKey.GetCooldownRemaining() == 0)
                {
                    input.Approach();
                }

                if (!stuckDetector.IsMoving())
                {
                    stuckDetector.Update();
                }
            }
            else
            {
                stopMoving.Stop();
            }
        }

        private bool SuccessfullPull()
        {
            return playerReader.TargetTarget is
                        TargetTargetEnum.Me or
                        TargetTargetEnum.Pet or
                        TargetTargetEnum.PartyOrPet ||
                        addonReader.CombatLog.DamageDoneGuid.ElapsedMs() < CastingHandler.GCD ||
                        playerReader.IsInMeleeRange();
        }

        private bool PullPrevention()
        {
            return blacklist.IsTargetBlacklisted() &&
                playerReader.TargetTarget is not
                TargetTargetEnum.None or
                TargetTargetEnum.Me or
                TargetTargetEnum.Pet or
                TargetTargetEnum.PartyOrPet;
        }

        private void Log(string text)
        {
            logger.LogInformation(text);
        }
    }
}