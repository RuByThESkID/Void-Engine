using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BoneLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.AI;
using MelonLoader;
using UnityEngine;

namespace VoidEngine
{
    internal static class DesktopStatsOverlay
    {
        private static readonly HashSet<int> DeadNpcIds = new();
        private static readonly HashSet<int> DeadRigIds = new();
        private static readonly HashSet<int> FusionDownedPlayerIds = new();
        private static bool _enabled;
        private static bool _engineEnabled;
        private static bool _diagnosticsEnabled;
        private static bool _fusionHookInstalled;
        private static bool _fusionHookFailureLogged;
        private static float _nextFusionHookAttempt;
        private static int _npcKills;
        private static int _fusionPlayerKills;
        private static int _deaths;
        private static int _styleScreenWidth = -1;
        private static GUIStyle? _titleStyle;
        private static GUIStyle? _versionStyle;
        private static GUIStyle? _counterStyle;

        internal static void Initialize()
        {
            Hooking.OnNPCBrainDie -= OnNpcDeath;
            Hooking.OnNPCBrainDie += OnNpcDeath;
            Hooking.OnNPCBrainResurrected -= OnNpcResurrected;
            Hooking.OnNPCBrainResurrected += OnNpcResurrected;
            Hooking.OnPlayerDeath -= OnPlayerDeath;
            Hooking.OnPlayerDeath += OnPlayerDeath;
            Hooking.OnPlayerResurrected -= OnPlayerResurrected;
            Hooking.OnPlayerResurrected += OnPlayerResurrected;
            Hooking.OnLevelLoaded += _ => ClearDeathDeduplication();

            TryInstallFusionHook();
        }

        internal static void Update()
        {
            if (!_fusionHookInstalled && Time.unscaledTime >= _nextFusionHookAttempt)
                TryInstallFusionHook();
        }

        internal static void SetEnabled(bool enabled)
        {
            _enabled = enabled;
        }

        internal static void SetEngineEnabled(bool enabled)
        {
            _engineEnabled = enabled;
        }

        internal static void SetDiagnosticsEnabled(bool enabled)
        {
            _diagnosticsEnabled = enabled;
        }

        internal static void ResetCounters()
        {
            _npcKills = 0;
            _fusionPlayerKills = 0;
            _deaths = 0;
            MelonLogger.Msg("[Void Engine] Desktop UI counters reset.");
        }

        internal static void Draw()
        {
            if (!_enabled || !_engineEnabled || Player.RigManager == null || Event.current == null ||
                Event.current.type != EventType.Repaint || Screen.width <= 0 || Screen.height <= 0)
            {
                return;
            }

            EnsureStyles();
            if (_titleStyle == null || _versionStyle == null || _counterStyle == null)
                return;

            float scale = Mathf.Clamp(Screen.width / 1920f, 0.65f, 1.6f);
            float titleHeight = 42f * scale;
            float versionHeight = 25f * scale;
            Rect titleRect = new(0f, 12f * scale, Screen.width, titleHeight);
            Rect versionRect = new(0f, titleRect.yMax - 1f * scale, Screen.width, versionHeight);

            float panelWidth = 310f * scale;
            float panelHeight = 112f * scale;
            float margin = 18f * scale;
            Rect panelRect = new(margin, Screen.height - panelHeight - margin, panelWidth, panelHeight);

            Color previousColor = GUI.color;
            GUI.color = new Color(0.025f, 0.018f, 0.045f, 0.72f);
            GUI.DrawTexture(panelRect, Texture2D.whiteTexture);

            GUI.color = Main.VoidworksPurple;
            GUI.Label(titleRect, "Voidworks", _titleStyle);

            GUI.color = new Color(0.82f, 0.67f, 1f, 1f);
            GUI.Label(versionRect, $"v{Main.Version}", _versionStyle);

            GUI.color = Color.white;
            float lineHeight = 27f * scale;
            float textX = panelRect.x + 14f * scale;
            float textY = panelRect.y + 8f * scale;
            float textWidth = panelRect.width - 28f * scale;
            GUI.Label(new Rect(textX, textY, textWidth, lineHeight), $"NPC Kills: {_npcKills}", _counterStyle);
            GUI.Label(new Rect(textX, textY + lineHeight, textWidth, lineHeight), $"Fusion Player Kills: {_fusionPlayerKills}", _counterStyle);
            GUI.Label(new Rect(textX, textY + lineHeight * 2f, textWidth, lineHeight), $"Deaths: {_deaths}", _counterStyle);
            GUI.color = previousColor;
        }

        private static void EnsureStyles()
        {
            if (_styleScreenWidth == Screen.width && _titleStyle != null && _versionStyle != null && _counterStyle != null)
                return;

            if (GUI.skin == null)
                return;

            _styleScreenWidth = Screen.width;
            float scale = Mathf.Clamp(Screen.width / 1920f, 0.65f, 1.6f);
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = Mathf.RoundToInt(36f * scale),
                fontStyle = FontStyle.Bold,
                richText = false
            };
            _versionStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = Mathf.RoundToInt(18f * scale),
                richText = false
            };
            _counterStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = Mathf.RoundToInt(19f * scale),
                richText = false
            };
        }

        private static void OnNpcDeath(AIBrain brain)
        {
            if (!_engineEnabled || brain == null)
                return;

            try
            {
                if (DeadNpcIds.Add(brain.GetInstanceID()))
                    _npcKills++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Void Engine] Could not record an NPC death: {ex.GetBaseException().Message}");
            }
        }

        private static void OnNpcResurrected(AIBrain brain)
        {
            if (brain == null)
                return;

            try
            {
                DeadNpcIds.Remove(brain.GetInstanceID());
            }
            catch
            {
            }
        }

        private static void OnPlayerDeath(RigManager rigManager)
        {
            if (!_engineEnabled || rigManager == null)
                return;

            try
            {
                RigManager? localRig = Player.RigManager;
                if (localRig != null && rigManager != localRig)
                    return;

                if (localRig == null && SubmenuPanelStyler.IsFusionSessionActive())
                    return;

                if (DeadRigIds.Add(rigManager.GetInstanceID()))
                    _deaths++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Void Engine] Could not record a player death: {ex.GetBaseException().Message}");
            }
        }

        private static void OnPlayerResurrected(RigManager rigManager)
        {
            if (rigManager == null)
                return;

            try
            {
                DeadRigIds.Remove(rigManager.GetInstanceID());
            }
            catch
            {
            }
        }

        private static void ClearDeathDeduplication()
        {
            DeadNpcIds.Clear();
            DeadRigIds.Clear();
            FusionDownedPlayerIds.Clear();
        }

        private static void TryInstallFusionHook()
        {
            _nextFusionHookAttempt = Time.unscaledTime + 5f;
            try
            {
                Assembly? fusionAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(assembly => string.Equals(
                        assembly.GetName().Name,
                        "LabFusion",
                        StringComparison.Ordinal
                    ));
                if (fusionAssembly == null)
                    return;

                Type? bridge = typeof(DesktopStatsOverlay).Assembly.GetType("VoidEngine.FusionDevToolSpawnBridge");
                MethodInfo? installHook = bridge?.GetMethod(
                    "InstallDesktopStatsHook",
                    BindingFlags.Static | BindingFlags.Public
                );
                if (installHook?.Invoke(null, null) is not bool installed || !installed)
                    return;

                _fusionHookInstalled = true;
                MelonLogger.Msg("[Void Engine] Fusion player-kill counter hook installed.");
            }
            catch (Exception ex)
            {
                if (_fusionHookFailureLogged)
                    return;

                _fusionHookFailureLogged = true;
                MelonLogger.Warning(
                    $"[Void Engine] Fusion player-kill counter hook could not be installed: {ex.GetBaseException().Message}"
                );
            }
        }

        internal static void RecordFusionPlayerAction(int victimId, string action, bool localPlayerIsKiller)
        {
            if (!_engineEnabled)
                return;

            bool relevantAction = action is "DYING_BY_OTHER_PLAYER" or "DEATH_BY_OTHER_PLAYER" or
                "RECOVERY" or "RESPAWN" or "DEATH";
            if (_diagnosticsEnabled && relevantAction)
            {
                MelonLogger.Msg(
                    $"[Void Engine] Fusion stats action: {action}, player={victimId}, " +
                    $"localKiller={localPlayerIsKiller}."
                );
            }

            if (action == "DYING_BY_OTHER_PLAYER")
            {
                if (localPlayerIsKiller && FusionDownedPlayerIds.Add(victimId))
                {
                    _fusionPlayerKills++;
                    if (_diagnosticsEnabled)
                        MelonLogger.Msg($"[Void Engine] Fusion player-kill counter is now {_fusionPlayerKills}.");
                }
            }
            else if (action is "RECOVERY" or "RESPAWN" or "DEATH")
            {
                FusionDownedPlayerIds.Remove(victimId);
            }
        }
    }
}
