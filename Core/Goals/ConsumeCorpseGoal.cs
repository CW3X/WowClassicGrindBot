﻿using Microsoft.Extensions.Logging;
using Core.GOAP;

namespace Core.Goals
{
    public partial class ConsumeCorpseGoal : GoapGoal
    {
        public override float Cost => 4.1f;

        private readonly ILogger logger;
        private readonly ClassConfiguration classConfig;

        public ConsumeCorpseGoal(ILogger logger, ClassConfiguration classConfig)
            : base(nameof(ConsumeCorpseGoal))
        {
            this.logger = logger;
            this.classConfig = classConfig;

            if (classConfig.Mode == Mode.AssistFocus)
            {
                AddPrecondition(GoapKey.dangercombat, false);
            }
            else
            {
                AddPrecondition(GoapKey.incombat, false);
            }

            AddPrecondition(GoapKey.producedcorpse, true);
            AddPrecondition(GoapKey.consumecorpse, false);

            AddEffect(GoapKey.producedcorpse, false);
            AddEffect(GoapKey.consumecorpse, true);

            if (classConfig.Loot)
            {
                AddEffect(GoapKey.shouldloot, true);

                if (classConfig.Skin)
                {
                    AddEffect(GoapKey.shouldskin, true);
                }
            }
        }

        public override void OnEnter()
        {
            LogConsume(logger);
            SendGoapEvent(new GoapStateEvent(GoapKey.consumecorpse, true));

            if (classConfig.Loot)
            {
                SendGoapEvent(new GoapStateEvent(GoapKey.shouldloot, true));
            }
        }

        [LoggerMessage(
            EventId = 100,
            Level = LogLevel.Information,
            Message = "----- Safe to consume a corpse.")]
        static partial void LogConsume(ILogger logger);
    }
}
