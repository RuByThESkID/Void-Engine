using System;
using System.Collections.Concurrent;
using System.Threading;
using BoneLib;
using MelonLoader;

namespace VoidEngine
{
    internal static class ConsoleFunctionCommands
    {
        private static readonly ConcurrentQueue<string> PendingCommands = new();
        private static int _initialized;

        internal static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0)
                return;

            try
            {
                Thread reader = new(ReadConsoleInput)
                {
                    IsBackground = true,
                    Name = "Void Engine Console Command Reader"
                };
                reader.Start();
                MelonLogger.Msg(
                    "[Void Engine] Console commands ready: function_avatar: <avatar name> | function_level: <level name> | function_giveitem: <item name> | function_tpspawnpoint: true | function_help"
                );
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Could not start console command input: {ex.GetBaseException().Message}"
                );
            }
        }

        internal static void Update()
        {
            while (PendingCommands.TryDequeue(out string? command))
                Execute(command);
        }

        private static void ReadConsoleInput()
        {
            try
            {
                while (true)
                {
                    string? line = Console.ReadLine();
                    if (line == null)
                        return;

                    if (!string.IsNullOrWhiteSpace(line))
                        PendingCommands.Enqueue(line.Trim());
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Console command input stopped: {ex.GetBaseException().Message}"
                );
            }
        }

        private static void Execute(string line)
        {
            if (!TrySplit(line, out string command, out string argument))
            {
                MelonLogger.Warning("[Void Engine] Could not parse console command. Type function_help for examples.");
                return;
            }

            command = command.Trim().ToLowerInvariant();
            argument = Unquote(argument.Trim());

            switch (command)
            {
                case "function_help":
                case "functionhelp":
                    MelonLogger.Msg("[Void Engine] Commands: function_avatar: <avatar name> | function_level: <level name> | function_giveitem: <item name> | function_tpspawnpoint: true");
                    break;

                case "function_avatar":
                    BoneMenuAvatarSearch.TryApplyAvatarByQuery(argument);
                    break;

                case "function_level":
                    BoneMenuMapSearch.TryLoadLevelByQuery(argument);
                    break;

                case "function_giveitem":
                    GiveItemCommand.TrySpawnByQuery(argument);
                    break;

                case "function_tpspawnpoint":
                    TeleportToSpawnPoint(argument);
                    break;

                default:
                    MelonLogger.Warning($"[Void Engine] Unknown command '{command}'. Type function_help for examples.");
                    break;
            }
        }

        private static bool TrySplit(string line, out string command, out string argument)
        {
            command = string.Empty;
            argument = string.Empty;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            int colon = line.IndexOf(':');
            int whitespace = line.IndexOfAny(new[] { ' ', '\t' });
            if (colon >= 0 && (whitespace < 0 || colon < whitespace))
            {
                command = line.Substring(0, colon);
                argument = line.Substring(colon + 1);
                return command.Length > 0;
            }

            if (whitespace < 0)
            {
                command = line;
                return true;
            }

            command = line.Substring(0, whitespace);
            argument = line.Substring(whitespace + 1);
            return command.Length > 0;
        }

        private static string Unquote(string argument)
        {
            if (argument.Length >= 2 &&
                ((argument[0] == '"' && argument[^1] == '"') ||
                 (argument[0] == '\'' && argument[^1] == '\'')))
            {
                return argument.Substring(1, argument.Length - 2).Trim();
            }

            return argument;
        }

        private static void TeleportToSpawnPoint(string argument)
        {
            if (!bool.TryParse(argument, out bool requested))
            {
                MelonLogger.Warning("[Void Engine] Usage: function_tpspawnpoint: true");
                return;
            }

            if (!requested)
            {
                MelonLogger.Msg("[Void Engine] Spawn point teleport skipped (argument was false).");
                return;
            }

            try
            {
                var rig = Player.RigManager;
                if (rig == null)
                {
                    MelonLogger.Warning("[Void Engine] Cannot teleport: player rig is not ready.");
                    return;
                }

                rig.Teleport(rig.checkpointPosition, rig.checkpointFwd, true);
                MelonLogger.Msg("[Void Engine] Teleported to the current spawn/checkpoint position.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Spawn point teleport failed: {ex.GetBaseException().Message}"
                );
            }
        }
    }
}
