﻿using Game;
using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using TextCopy;

namespace Core
{
    public class ExecGameCommand
    {
        private readonly ILogger logger;
        private readonly WowProcessInput wowProcessInput;
        private readonly Random random = new();
        private readonly CancellationTokenSource cts = new();

        public ExecGameCommand(ILogger logger, WowProcessInput wowProcessInput)
        {
            this.logger = logger;
            this.wowProcessInput = wowProcessInput;
        }

        public void Run(string content)
        {
            wowProcessInput.SetForegroundWindow();
            logger.LogInformation(content);

            ClipboardService.SetText(content);
            Wait(100, 250);

            // Open chat inputbox
            wowProcessInput.KeyPress(ConsoleKey.Enter, random.Next(50, 100));

            wowProcessInput.PasteFromClipboard();
            Wait(100, 250);

            // Close chat inputbox
            wowProcessInput.KeyPress(ConsoleKey.Enter, random.Next(50, 100));
            Wait(100, 250);
        }

        private void Wait(int min, int max)
        {
            cts.Token.WaitHandle.WaitOne(random.Next(min, max));
        }
    }
}
