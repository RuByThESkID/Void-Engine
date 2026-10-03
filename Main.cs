using System;
using System.Collections.Generic;
using BoneLib;
using BoneLib.BoneMenu;
using HarmonyLib;
using Il2CppSLZ.Bonelab;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Page = BoneLib.BoneMenu.Page;

[assembly: MelonInfo(typeof(VoidEngine.Main), "Void Engine", "1.0.4", "RuByThESkID")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]
[assembly: MelonOptionalDependencies(new string[] { "LabFusion" })]

namespace VoidEngine
{
    public class Main : MelonMod
    {
        internal static readonly Color VoidworksPurple = new(0.75f, 0.2f, 1.0f, 1.0f);
        internal static readonly Color VoidworksBlue = new(0.22f, 0.29f, 0.90f, 1.0f);
        internal static readonly Color VoidworksBluePurple = new(0.49f, 0.24f, 0.95f, 1.0f);
        internal const string Version = "1.0.4";
        private static Page? VoidEnginePage;
        private static Page? DeadeyeDistancePage;
        private static readonly List<Element> DeadeyeDistanceOptions = new();
        private static string _selectedDeadeyeDistance = "50m";
        private static MelonPreferences_Category? _preferences;
        private static MelonPreferences_Entry<bool>? _engineEnabledPreference;
        private static MelonPreferences_Entry<int>? _physicsQualityPreference;
        private static MelonPreferences_Entry<bool>? _autoDespawnPreference;
        private static MelonPreferences_Entry<bool>? _outsideRadialColorPreference;
        private static MelonPreferences_Entry<bool>? _deadeyeDisabledPreference;
        private static MelonPreferences_Entry<bool>? _deadeyeSlowMoRequiredPreference;
        private static MelonPreferences_Entry<bool>? _desktopUiPreference;
        private static MelonPreferences_Entry<int>? _deadeyeDistancePreference;
        private static MelonPreferences_Entry<bool>? _detailedPhysicsLogsPreference;
        private static MelonPreferences_Entry<bool>? _bodyRigLogsPreference;
        private static MelonPreferences_Entry<bool>? _fingerLogsPreference;
        private static MelonPreferences_Entry<bool>? _heldJointLogsPreference;
        private static MelonPreferences_Entry<bool>? _deadeyeLogsPreference;
        private static MelonPreferences_Entry<bool>? _devToolLogsPreference;
        private static MelonPreferences_Entry<bool>? _radialUiLogsPreference;
        private static MelonPreferences_Entry<bool>? _desktopUiLogsPreference;
        private static readonly (string Label, float Meters)[] DeadeyeDistancePresets =
        {
            ("50m", 50f),
            ("100m", 100f),
            ("500m", 500f),
            ("1000m", 1000f),
            ("Limitless", float.PositiveInfinity)
        };

        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("========================================");
            MelonLogger.Msg($"Void Engine {Version} initializing...");
            MelonLogger.Msg("========================================");

            InitializePreferences();
            Hooking.OnLevelLoaded += _ =>
            {
                PhysicsManager.OnLevelLoaded();
                Deadeye.OnLevelLoaded();
                SpawnerSearch.OnLevelLoaded();
            };
            ConsoleFunctionCommands.Initialize();
            PhysRigFusion.Initialize();

            VoidEnginePage = Page.Root.CreatePage(
                $"Void Engine {Version}",
                VoidworksPurple
            );

            VoidEnginePage.CreateBool(
                "Void Engine",
                VoidworksPurple,
                _engineEnabledPreference?.Value ?? false,
                OnPhysicsToggle
            );

            VoidEnginePage.CreateEnum(
                "Physics Quality (Adaptive recommended)",
                VoidworksPurple,
                (Enum)PhysicsManager.Quality,
                OnPhysicsQualityChanged
            );

            AmmoCounter.Initialize();
            AmmoCounter.AddPouchSideControl(VoidEnginePage);
            VoidEnginePage.CreateBool(
                "Dev Tool Auto-Despawn",
                VoidworksPurple,
                _autoDespawnPreference?.Value ?? true,
                OnAutoDespawnChanged
            );

            BoneMenuAvatarSearch.Initialize(VoidEnginePage);
            BoneMenuMapSearch.Initialize(VoidEnginePage);

            Page advancedSettingsPage = VoidEnginePage.CreatePage("Advanced Settings", VoidworksPurple);
            advancedSettingsPage.CreateBool(
                "Coloring Outside of Radial Menu",
                VoidworksPurple,
                _outsideRadialColorPreference?.Value ?? true,
                OnOutsideRadialColorChanged
            );
            advancedSettingsPage.CreateBool(
                "Deadeye",
                VoidworksPurple,
                !(_deadeyeDisabledPreference?.Value ?? false),
                OnDeadeyeEnabledChanged
            );
            advancedSettingsPage.CreateBool(
                "Slo-mo Required For Deadeye",
                VoidworksPurple,
                _deadeyeSlowMoRequiredPreference?.Value ?? true,
                OnDeadeyeSlowMoRequirementChanged
            );
            advancedSettingsPage.CreateBool(
                "Desktop UI",
                VoidworksPurple,
                _desktopUiPreference?.Value ?? true,
                OnDesktopUiChanged
            );

            int savedDistanceIndex = Mathf.Clamp(
                _deadeyeDistancePreference?.Value ?? 0,
                0,
                DeadeyeDistancePresets.Length - 1
            );
            _selectedDeadeyeDistance = DeadeyeDistancePresets[savedDistanceIndex].Label;
            DeadeyeDistancePage = advancedSettingsPage.CreatePage(
                "Deadeye Effective Distance",
                VoidworksPurple,
                8,
                true
            );
            RefreshDeadeyeDistanceOptions();
            advancedSettingsPage.CreateFunction(
                "Reset UI",
                VoidworksPurple,
                DesktopStatsOverlay.ResetCounters
            );

            Page devLoggerPage = advancedSettingsPage.CreatePage("Dev Logger", VoidworksPurple);
            devLoggerPage.CreateBool(
                "Detailed Physics Logs",
                VoidworksPurple,
                _detailedPhysicsLogsPreference?.Value ?? false,
                OnDetailedPhysicsLogsChanged
            );
            devLoggerPage.CreateBool(
                "Log Body Rig",
                VoidworksPurple,
                _bodyRigLogsPreference?.Value ?? false,
                OnBodyRigLogsChanged
            );
            devLoggerPage.CreateBool(
                "Log Finger Diagnostics",
                VoidworksPurple,
                _fingerLogsPreference?.Value ?? false,
                OnFingerLogsChanged
            );
            devLoggerPage.CreateBool(
                "Log Held Joint Probe",
                VoidworksPurple,
                _heldJointLogsPreference?.Value ?? false,
                OnHeldJointLogsChanged
            );
            devLoggerPage.CreateBool(
                "Log Deadeye Diagnostics",
                VoidworksPurple,
                _deadeyeLogsPreference?.Value ?? false,
                OnDeadeyeLogsChanged
            );
            devLoggerPage.CreateBool(
                "Log Dev Tool Auto-Despawn",
                VoidworksPurple,
                _devToolLogsPreference?.Value ?? false,
                OnDevToolLogsChanged
            );
            devLoggerPage.CreateBool(
                "Log Radial UI Hierarchy",
                VoidworksPurple,
                _radialUiLogsPreference?.Value ?? false,
                OnRadialUiLogsChanged
            );
            devLoggerPage.CreateBool(
                "Log Desktop UI Counters",
                VoidworksPurple,
                _desktopUiLogsPreference?.Value ?? false,
                OnDesktopUiLogsChanged
            );
            Page ammoCounterPlacementPage = advancedSettingsPage.CreatePage(
                "Ammo Counter Placement",
                VoidworksPurple,
                8,
                true
            );
            AmmoCounter.AddPlacementControls(ammoCounterPlacementPage);
            AmmoCounter.AddPlacementLoggerControl(devLoggerPage);

            VoidEngineUI.Initialize();
            DesktopStatsOverlay.Initialize();

            ApplySavedFeatureSettings();
            if (_engineEnabledPreference?.Value == true)
                OnPhysicsToggle(true);

            MelonLogger.Msg("[Void Engine] BoneMenu initialized.");
            MelonLogger.Msg("[Void Engine] Waiting for Voidworks to be enabled...");
        }

        public override void OnUpdate()
        {
            ConsoleFunctionCommands.Update();
            DesktopStatsOverlay.Update();
            SpawnerSearch.Update();
            SubmenuPanelStyler.Update();
            VoidEngineUI.Update();
            AmmoCounter.Update();
            DevChanges.Update();
            PhysRig.Update();
            PhysRigFusion.Update();
            PhysicsManager.UpdateHeldObjectCollision();
            Deadeye.Update();
        }

        public override void OnGUI()
        {
            DesktopStatsOverlay.Draw();
        }

        private static void OnPhysicsToggle(bool enabled)
        {
            SavePreference(_engineEnabledPreference, enabled);
            MelonLogger.Msg(
                $"[Void Engine] Voidworks toggle changed: {(enabled ? "ON" : "OFF")}"
            );

            if (enabled)
            {
                PhysicsManager.SetEnabled(true);
                bool active = PhysicsManager.Enabled;
                DesktopStatsOverlay.SetEngineEnabled(active);

                PhysicsManager.SetCollisionEnabled(active);
                PhysicsManager.SetBodyCollisionEnabled(active);
                PhysRig.SetFingerFixEnabled(active);
                PhysRig.SetGrabHoverReductionEnabled(active);
                DevChanges.SetVoidworksEnabled(active);
                AmmoCounter.SetEnabled(active);
                SpawnerSearch.SetEnabled(active);
                VoidEngineUI.SetEnabled(active);
                Deadeye.SetVoidworksEnabled(active);

                if (!active)
                {
                    SavePreference(_engineEnabledPreference, false);
                    MelonLogger.Warning("[Void Engine] Voidworks features were not enabled because physics setup failed.");
                }

                return;
            }

            DesktopStatsOverlay.SetEngineEnabled(false);
            PhysRig.SetFingerFixEnabled(false);
            PhysRig.SetGrabHoverReductionEnabled(false);
            DevChanges.SetVoidworksEnabled(false);
            AmmoCounter.SetEnabled(false);
            SpawnerSearch.SetEnabled(false);
            Deadeye.SetVoidworksEnabled(false);
            PhysicsManager.SetCollisionEnabled(false);
            PhysicsManager.SetBodyCollisionEnabled(false);
            PhysicsManager.SetEnabled(false);
            VoidEngineUI.SetEnabled(false);
        }

        private static void InitializePreferences()
        {
            try
            {
                _preferences = MelonPreferences.CreateCategory("VoidEngine", "Void Engine");
                _engineEnabledPreference = _preferences.CreateEntry("EngineEnabled", false);
                _physicsQualityPreference = _preferences.CreateEntry(
                    "PhysicsQuality",
                    (int)PhysicsManager.PhysicsQuality.Adaptive
                );
                _autoDespawnPreference = _preferences.CreateEntry("DevToolAutoDespawn", true);
                _outsideRadialColorPreference = _preferences.CreateEntry("OutsideRadialColoring", true);
                _deadeyeDisabledPreference = _preferences.CreateEntry("DeadeyeDisabled", false);
                _deadeyeSlowMoRequiredPreference = _preferences.CreateEntry("DeadeyeSlowMoRequired", true);
                _desktopUiPreference = _preferences.CreateEntry("DesktopUIEnabled", true);
                _deadeyeDistancePreference = _preferences.CreateEntry("DeadeyeDistancePreset", 0);
                _detailedPhysicsLogsPreference = _preferences.CreateEntry("DetailedPhysicsLogs", false);
                _bodyRigLogsPreference = _preferences.CreateEntry("BodyRigLogs", false);
                _fingerLogsPreference = _preferences.CreateEntry("FingerLogs", false);
                _heldJointLogsPreference = _preferences.CreateEntry("HeldJointLogs", false);
                _deadeyeLogsPreference = _preferences.CreateEntry("DeadeyeLogs", false);
                _devToolLogsPreference = _preferences.CreateEntry("DevToolLogs", false);
                _radialUiLogsPreference = _preferences.CreateEntry("RadialUiLogs", false);
                _desktopUiLogsPreference = _preferences.CreateEntry("DesktopUiLogs", false);

                int qualityValue = _physicsQualityPreference.Value;
                if (Enum.IsDefined(typeof(PhysicsManager.PhysicsQuality), qualityValue))
                    PhysicsManager.SetQuality((PhysicsManager.PhysicsQuality)qualityValue);
                else
                    _physicsQualityPreference.Value = (int)PhysicsManager.PhysicsQuality.Adaptive;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Void Engine] Could not load settings: {ex.GetBaseException().Message}");
            }
        }

        private static void ApplySavedFeatureSettings()
        {
            DevChanges.SetAutoDespawnEnabled(_autoDespawnPreference?.Value ?? true);
            VoidEngineUI.SetOutsideRadialColorEnabled(_outsideRadialColorPreference?.Value ?? true);
            Deadeye.SetSlowMotionRequired(_deadeyeSlowMoRequiredPreference?.Value ?? true);
            Deadeye.SetManuallyDisabled(_deadeyeDisabledPreference?.Value ?? false);
            DesktopStatsOverlay.SetEnabled(_desktopUiPreference?.Value ?? true);
            DesktopStatsOverlay.SetEngineEnabled(_engineEnabledPreference?.Value ?? false);
            int distanceIndex = Mathf.Clamp(
                _deadeyeDistancePreference?.Value ?? 0,
                0,
                DeadeyeDistancePresets.Length - 1
            );
            _selectedDeadeyeDistance = DeadeyeDistancePresets[distanceIndex].Label;
            Deadeye.SetMaximumTargetDistance(DeadeyeDistancePresets[distanceIndex].Meters);

            PhysicsManager.SetDetailedLoggingEnabled(_detailedPhysicsLogsPreference?.Value ?? false);
            PhysicsManager.SetHeldJointProbeLoggingEnabled(_heldJointLogsPreference?.Value ?? false);
            PhysRig.SetDiagnosticsEnabled(_bodyRigLogsPreference?.Value ?? false);
            PhysRig.SetFingerDiagnosticsEnabled(_fingerLogsPreference?.Value ?? false);
            Deadeye.SetDiagnosticsEnabled(_deadeyeLogsPreference?.Value ?? false);
            DevChanges.SetDiagnosticsEnabled(_devToolLogsPreference?.Value ?? false);
            VoidEngineUI.SetHierarchyLoggingEnabled(_radialUiLogsPreference?.Value ?? false);
            DesktopStatsOverlay.SetDiagnosticsEnabled(_desktopUiLogsPreference?.Value ?? false);
        }

        private static void OnPhysicsQualityChanged(Enum value)
        {
            if (value is not PhysicsManager.PhysicsQuality quality)
                return;

            PhysicsManager.SetQuality(quality);
            SavePreference(_physicsQualityPreference, (int)quality);
        }

        private static void OnAutoDespawnChanged(bool enabled)
        {
            DevChanges.SetAutoDespawnEnabled(enabled);
            SavePreference(_autoDespawnPreference, enabled);
        }

        private static void OnOutsideRadialColorChanged(bool enabled)
        {
            VoidEngineUI.SetOutsideRadialColorEnabled(enabled);
            SavePreference(_outsideRadialColorPreference, enabled);
        }

        private static void OnDeadeyeEnabledChanged(bool enabled)
        {
            bool disabled = !enabled;
            Deadeye.SetManuallyDisabled(disabled);
            SavePreference(_deadeyeDisabledPreference, disabled);
        }

        private static void OnDeadeyeSlowMoRequirementChanged(bool required)
        {
            Deadeye.SetSlowMotionRequired(required);
            SavePreference(_deadeyeSlowMoRequiredPreference, required);
        }

        private static void OnDesktopUiChanged(bool enabled)
        {
            DesktopStatsOverlay.SetEnabled(enabled);
            SavePreference(_desktopUiPreference, enabled);
        }

        private static void OnDetailedPhysicsLogsChanged(bool enabled)
        {
            PhysicsManager.SetDetailedLoggingEnabled(enabled);
            SavePreference(_detailedPhysicsLogsPreference, enabled);
        }

        private static void OnBodyRigLogsChanged(bool enabled)
        {
            PhysRig.SetDiagnosticsEnabled(enabled);
            SavePreference(_bodyRigLogsPreference, enabled);
        }

        private static void OnFingerLogsChanged(bool enabled)
        {
            PhysRig.SetFingerDiagnosticsEnabled(enabled);
            SavePreference(_fingerLogsPreference, enabled);
        }

        private static void OnHeldJointLogsChanged(bool enabled)
        {
            PhysicsManager.SetHeldJointProbeLoggingEnabled(enabled);
            SavePreference(_heldJointLogsPreference, enabled);
        }

        private static void OnDeadeyeLogsChanged(bool enabled)
        {
            Deadeye.SetDiagnosticsEnabled(enabled);
            SavePreference(_deadeyeLogsPreference, enabled);
        }

        private static void OnDevToolLogsChanged(bool enabled)
        {
            DevChanges.SetDiagnosticsEnabled(enabled);
            SavePreference(_devToolLogsPreference, enabled);
        }

        private static void OnRadialUiLogsChanged(bool enabled)
        {
            VoidEngineUI.SetHierarchyLoggingEnabled(enabled);
            SavePreference(_radialUiLogsPreference, enabled);
        }

        private static void OnDesktopUiLogsChanged(bool enabled)
        {
            DesktopStatsOverlay.SetDiagnosticsEnabled(enabled);
            SavePreference(_desktopUiLogsPreference, enabled);
        }

        private static void SavePreference<T>(MelonPreferences_Entry<T>? entry, T value)
        {
            if (entry == null)
                return;

            try
            {
                entry.Value = value;
                MelonPreferences.Save();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Void Engine] Could not save a setting: {ex.GetBaseException().Message}");
            }
        }

        private static void RefreshDeadeyeDistanceOptions()
        {
            if (DeadeyeDistancePage == null)
                return;

            foreach (Element option in DeadeyeDistanceOptions)
                DeadeyeDistancePage.Remove(option);
            DeadeyeDistanceOptions.Clear();

            foreach ((string label, float meters) in DeadeyeDistancePresets)
            {
                string optionLabel = label;
                float optionMeters = meters;
                string displayLabel = string.Equals(optionLabel, _selectedDeadeyeDistance, StringComparison.Ordinal)
                    ? $"{optionLabel} (current)"
                    : optionLabel;

                DeadeyeDistanceOptions.Add(DeadeyeDistancePage.CreateFunction(
                    displayLabel,
                    VoidworksPurple,
                    () =>
                    {
                        _selectedDeadeyeDistance = optionLabel;
                        Deadeye.SetMaximumTargetDistance(optionMeters);
                        int distanceIndex = Array.FindIndex(
                            DeadeyeDistancePresets,
                            preset => string.Equals(preset.Label, optionLabel, StringComparison.Ordinal)
                        );
                        if (distanceIndex >= 0)
                            SavePreference(_deadeyeDistancePreference, distanceIndex);
                        RefreshDeadeyeDistanceOptions();
                    }
                ));
            }
        }
    }

    public static class VoidEngineUI
    {
        private static bool _enabled;
        private static bool _outsideRadialColorEnabled = true;
        private static bool _hierarchyLoggingEnabled;
        private static readonly Dictionary<Graphic, Color> OriginalGraphicColors = new();
        private static readonly Dictionary<TMP_Text, string> OriginalLabelTexts = new();
        private static readonly Dictionary<PageItemView, Color> OriginalRadialItemColors = new();
        private static readonly Dictionary<Renderer, bool> OriginalCenterCancelRendererStates = new();
        private static GameObject? _brandingObject;

        public static void SetHierarchyLoggingEnabled(bool enabled)
        {
            _hierarchyLoggingEnabled = enabled;
            SubmenuPanelStyler.SetHierarchyLoggingEnabled(enabled);
        }
        private static float _nextBrandingCheck;

        public static void Initialize()
        {
            Hooking.OnUIRigCreated += OnUIRigCreated;
            Hooking.OnLevelLoaded += _ =>
            {
                RefreshRadialMenu();
                if (_enabled)
                    SubmenuPanelStyler.Refresh();
            };

            try
            {
                new HarmonyLib.Harmony("VoidEngine.CenterCancelIndicator").PatchAll(
                    typeof(VoidEngineUI).Assembly
                );
                MelonLogger.Msg("[Void Engine] Radial cancel indicator patches installed.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Could not install radial cancel indicator patches: {ex.Message}"
                );
            }

            MelonLogger.Msg("[Void Engine] Radial menu hooks registered.");
        }

        public static void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            RefreshRadialMenu();
        }

        public static void SetOutsideRadialColorEnabled(bool enabled)
        {
            _outsideRadialColorEnabled = enabled;
            RefreshRadialMenu();
        }

        public static void Update()
        {
            if (!_enabled)
                return;

            KeepCenterCancelIndicatorHidden();

            if (Time.time < _nextBrandingCheck)
                return;

            if (_brandingObject != null)
            {
                _nextBrandingCheck = Time.time + 0.5f;
                TMP_Text? brandingText = _brandingObject.GetComponent<TMP_Text>();
                if (brandingText != null)
                {
                    if (!_brandingObject.activeSelf)
                        _brandingObject.SetActive(true);

                    brandingText.enabled = true;
                    brandingText.color = Main.VoidworksPurple;
                    brandingText.text = $"Voidworks\n<size=70%>v{Main.Version}</size>";
                    return;
                }

                _brandingObject = null;
            }

            _nextBrandingCheck = Time.time + 2f;
            try
            {
                foreach (GameObject sceneObject in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
                {
                    if (sceneObject == null ||
                        !sceneObject.name.Contains("CANVAS_RADIALUI", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Transform? existingBranding = sceneObject.transform.Find("VOIDWORKS_BRANDING");
                    if (existingBranding != null)
                    {
                        _brandingObject = existingBranding.gameObject;
                        _brandingObject.SetActive(true);
                        MelonLogger.Msg("[Void Engine] Existing Voidworks branding label reactivated.");
                        return;
                    }

                    TMP_Text[] labels = sceneObject.GetComponentsInChildren<TMP_Text>(true);
                    if (TryAddBranding(sceneObject.transform, labels))
                    {
                        MelonLogger.Msg("[Void Engine] Voidworks branding label recreated.");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Void Engine] Branding recovery failed: {ex.Message}");
            }
        }

        private static void OnUIRigCreated()
        {
            MelonLogger.Msg("[Void Engine] UIRig created; refreshing radial menu.");
            RefreshRadialMenu();
        }

        private static void RefreshRadialMenu()
        {
            RestoreOriginals();
            SubmenuPanelStyler.SetEnabled(_enabled && _outsideRadialColorEnabled);

            if (!_enabled)
                return;

            try
            {
                var sceneObjects = UnityEngine.Object.FindObjectsOfType<GameObject>(true);
                int radialCanvasCount = 0;
                int imageCount = 0;
                int labelCount = 0;
                int blackBarCount = ApplyRadialOptionBarColors();
                int brandingCount = 0;

                foreach (GameObject sceneObject in sceneObjects)
                {
                    if (sceneObject == null ||
                        !sceneObject.name.Contains("CANVAS_RADIALUI", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    radialCanvasCount++;

                    foreach (Image image in sceneObject.GetComponentsInChildren<Image>(true))
                    {
                        if (image == null || IsInSelectionPanel(image.transform))
                            continue;

                        SetGraphicColor(image, Main.VoidworksPurple);
                        imageCount++;
                    }

                    foreach (TMP_Text label in sceneObject.GetComponentsInChildren<TMP_Text>(true))
                    {
                        if (label == null || IsInSelectionPanel(label.transform))
                        {
                            continue;
                        }

                        string labelText = label.text?.Trim() ?? string.Empty;
                        if (string.Equals(labelText, "Bonelab", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!OriginalLabelTexts.ContainsKey(label))
                                OriginalLabelTexts.Add(label, label.text!);

                            label.text = "Voidworks";
                            labelCount++;
                        }

                        SetGraphicColor(label, Main.VoidworksPurple);
                    }

                    if (TryAddBranding(sceneObject.transform, sceneObject.GetComponentsInChildren<TMP_Text>(true)))
                    {
                        brandingCount++;
                    }

                    if (_hierarchyLoggingEnabled && (blackBarCount == 0 || brandingCount == 0))
                        DumpRadialHierarchy(sceneObject);
                }

                MelonLogger.Msg(
                    $"[Void Engine] Radial menu refreshed: canvases={radialCanvasCount}, " +
                    $"images tinted={imageCount}, labels renamed={labelCount}, " +
                    $"option bars recolored={blackBarCount}, branding labels added={brandingCount}."
                );

                int hiddenCenterIndicators = CacheAndHideCenterCancelIndicator();
                MelonLogger.Msg(
                    $"[Void Engine] Center cancel indicator renderers suppressed={hiddenCenterIndicators}."
                );
            }
            catch (Exception ex)
            {
                RestoreOriginals();
                MelonLogger.Error($"[Void Engine] Radial menu styling failed: {ex}");
            }
        }

        private static void RestoreOriginals()
        {
            if (_brandingObject != null)
            {
                UnityEngine.Object.Destroy(_brandingObject);
                _brandingObject = null;
            }

            foreach (KeyValuePair<Graphic, Color> entry in OriginalGraphicColors)
            {
                try
                {
                    if (entry.Key != null)
                        entry.Key.color = entry.Value;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Could not restore a radial menu graphic: {ex.Message}"
                    );
                }
            }

            OriginalGraphicColors.Clear();

            foreach (KeyValuePair<PageItemView, Color> entry in OriginalRadialItemColors)
            {
                try
                {
                    if (entry.Key != null)
                        entry.Key.color2 = entry.Value;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Could not restore a radial menu option bar: {ex.Message}"
                    );
                }
            }

            OriginalRadialItemColors.Clear();

            foreach (KeyValuePair<Renderer, bool> entry in OriginalCenterCancelRendererStates)
            {
                try
                {
                    if (entry.Key != null)
                        entry.Key.enabled = entry.Value;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Could not restore the radial center indicator: {ex.Message}"
                    );
                }
            }

            OriginalCenterCancelRendererStates.Clear();

            foreach (KeyValuePair<TMP_Text, string> entry in OriginalLabelTexts)
            {
                try
                {
                    if (entry.Key != null)
                        entry.Key.text = entry.Value;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Could not restore a radial menu label: {ex.Message}"
                    );
                }
            }

            OriginalLabelTexts.Clear();
        }

        private static int CacheAndHideCenterCancelIndicator()
        {
            int rendererCount = 0;

            foreach (PageView pageView in GameObject.FindObjectsOfType<PageView>(true))
            {
                if (pageView == null)
                    continue;

                rendererCount += CacheAndHideCenterCancelIndicator(pageView);
            }

            return rendererCount;
        }

        internal static void SuppressCenterCancelIndicator(PageView pageView)
        {
            if (!_enabled || pageView == null)
                return;

            CacheAndHideCenterCancelIndicator(pageView);
        }

        private static int CacheAndHideCenterCancelIndicator(PageView pageView)
        {
            int rendererCount = 0;

            GameObject? textCanvas = pageView.TextCanvas;
            PageElementView? cancelButton = pageView.cancelButton;
            if (textCanvas == null || cancelButton == null)
                return rendererCount;

            if (!textCanvas.name.Contains("CANVAS_RADIALUI", StringComparison.OrdinalIgnoreCase))
                return rendererCount;

            var cancelElements = cancelButton.elements;
            if (cancelElements == null)
                return rendererCount;

            foreach (HighlightUI element in cancelElements)
            {
                if (element == null)
                    continue;

                foreach (Renderer renderer in element.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null)
                        continue;

                    if (!OriginalCenterCancelRendererStates.ContainsKey(renderer))
                        OriginalCenterCancelRendererStates.Add(renderer, renderer.enabled);

                    renderer.enabled = false;
                    rendererCount++;
                }
            }

            return rendererCount;
        }

        private static void KeepCenterCancelIndicatorHidden()
        {
            foreach (Renderer renderer in OriginalCenterCancelRendererStates.Keys)
            {
                if (renderer != null && renderer.enabled)
                    renderer.enabled = false;
            }
        }

        private static void SetGraphicColor(Graphic graphic, Color color)
        {
            if (graphic == null)
                return;

            if (!OriginalGraphicColors.ContainsKey(graphic))
                OriginalGraphicColors.Add(graphic, graphic.color);

            graphic.color = color;
        }

        private static bool IsInSelectionPanel(Transform transform)
        {
            if (SubmenuPanelStyler.IsBodyMallInterface(transform) ||
                SubmenuPanelStyler.IsMultiplayerInterface(transform))
                return true;

            for (Transform? current = transform; current != null; current = current.parent)
            {
                if (SubmenuPanelStyler.IsLevelSelectorName(current.name) ||
                    SubmenuPanelStyler.IsAvatarSelectorName(current.name))
                    return true;
            }

            return false;
        }

        private static int ApplyRadialOptionBarColors()
        {
            int changedCount = 0;

            foreach (PageView pageView in GameObject.FindObjectsOfType<PageView>(true))
            {
                if (pageView == null ||
                    pageView.TextCanvas == null ||
                    !pageView.TextCanvas.name.Contains("CANVAS_RADIALUI", StringComparison.OrdinalIgnoreCase) ||
                    pageView.buttons == null)
                    continue;

                foreach (PageItemView view in pageView.buttons)
                {
                    if (view == null ||
                        !IsRadialOptionName(view.gameObject.name) ||
                        SubmenuPanelStyler.IsMultiplayerInterface(view.transform))
                        continue;

                    if (!OriginalRadialItemColors.ContainsKey(view))
                        OriginalRadialItemColors.Add(view, view.color2);

                    view.color2 = Color.blue;
                    changedCount++;
                }
            }

            return changedCount;
        }

        private static bool IsRadialOptionName(string name)
        {
            return name == "button_Region_N" ||
                   name == "button_Region_NE" ||
                   name == "button_Region_E" ||
                   name == "button_Region_SE" ||
                   name == "button_Region_S" ||
                   name == "button_Region_SW" ||
                   name == "button_Region_W" ||
                   name == "button_Region_NW" ||
                   name == "button_cancel";
        }

        private static void DumpRadialHierarchy(GameObject radialRoot)
        {
            MelonLogger.Msg($"[Void Engine] Radial UI hierarchy probe: root={radialRoot.name}");

            foreach (Transform node in radialRoot.GetComponentsInChildren<Transform>(true))
            {
                string path = GetRelativePath(node, radialRoot.transform);
                TMP_Text? tmp = node.GetComponent<TMP_Text>();
                UnityEngine.UI.Text? legacyText = node.GetComponent<UnityEngine.UI.Text>();
                Graphic? graphic = node.GetComponent<Graphic>();
                Button? button = node.GetComponent<Button>();
                Renderer? renderer = node.GetComponent<Renderer>();

                if (tmp != null)
                    MelonLogger.Msg($"[Void Engine] UI node TMP | {path} | text='{TrimForLog(tmp.text)}'");
                if (legacyText != null)
                    MelonLogger.Msg($"[Void Engine] UI node Text | {path} | text='{TrimForLog(legacyText.text)}'");
                if (graphic != null)
                    MelonLogger.Msg($"[Void Engine] UI node Graphic | {path} | type={graphic.GetType().Name}");
                if (button != null)
                    MelonLogger.Msg(
                        $"[Void Engine] UI node Button | {path} | target={button.targetGraphic?.name ?? "none"}"
                    );
                if (renderer != null)
                    MelonLogger.Msg($"[Void Engine] UI node Renderer | {path} | type={renderer.GetType().Name}");
            }
        }

        private static string GetRelativePath(Transform node, Transform root)
        {
            string path = node.name;
            Transform? parent = node.parent;
            while (parent != null && parent != root)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        private static string TrimForLog(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string singleLine = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return singleLine.Length <= 80 ? singleLine : singleLine.Substring(0, 80);
        }

        private static bool TryAddBranding(Transform canvasRoot, TMP_Text[] labels)
        {
            TMP_Text? textTemplate = null;
            foreach (TMP_Text label in labels)
            {
                if (label != null && string.Equals(label.gameObject.name, "text_button", StringComparison.Ordinal))
                {
                    textTemplate = label;
                    break;
                }
            }

            PageItemView? avatarItem = FindPageItemView("button_Region_NW", canvasRoot);
            PageItemView? levelItem = FindPageItemView("button_Region_NE", canvasRoot);
            if (textTemplate == null || avatarItem == null || levelItem == null)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Branding anchor lookup failed: template={textTemplate != null}, " +
                    $"avatar={avatarItem != null}, level={levelItem != null}."
                );
                return false;
            }

            RectTransform templateRect = textTemplate.rectTransform;
            Vector3 midpoint = (avatarItem.transform.position + levelItem.transform.position) * 0.5f;
            float labelHeightWorld = Mathf.Max(
                Vector3.Distance(
                    templateRect.TransformPoint(new Vector3(0f, templateRect.rect.height * 0.5f, 0f)),
                    templateRect.TransformPoint(new Vector3(0f, -templateRect.rect.height * 0.5f, 0f))
                ),
                0.01f
            );
            Vector3 labelPosition = midpoint - canvasRoot.up.normalized * (labelHeightWorld * 0.2f);

            GameObject branding = UnityEngine.Object.Instantiate(
                textTemplate.gameObject,
                canvasRoot
            );
            branding.name = "VOIDWORKS_BRANDING";
            branding.SetActive(true);

            RectTransform brandingRect = branding.GetComponent<RectTransform>();
            brandingRect.position = labelPosition;
            brandingRect.rotation = templateRect.rotation;
            brandingRect.localScale = templateRect.localScale;
            brandingRect.sizeDelta = new Vector2(templateRect.rect.width * 1.8f, templateRect.rect.height * 2.2f);
            brandingRect.SetAsLastSibling();

            TMP_Text brandingText = branding.GetComponent<TMP_Text>();
            brandingText.enabled = true;
            brandingText.font = textTemplate.font;
            brandingText.fontSharedMaterial = textTemplate.fontSharedMaterial;
            brandingText.fontSize = textTemplate.fontSize;
            brandingText.alignment = TextAlignmentOptions.Center;
            brandingText.color = Main.VoidworksPurple;
            brandingText.raycastTarget = false;
            brandingText.text = $"Voidworks\n<size=70%>v{Main.Version}</size>";

            _brandingObject = branding;
            return true;
        }

        private static PageItemView? FindPageItemView(string objectName, Transform canvasRoot)
        {
            PageItemView? closestView = null;
            float closestDistance = float.MaxValue;

            foreach (PageItemView view in GameObject.FindObjectsOfType<PageItemView>(true))
            {
                if (view == null || view.gameObject.name != objectName)
                    continue;

                if (view.transform.IsChildOf(canvasRoot))
                    return view;

                float distance = (view.transform.position - canvasRoot.position).sqrMagnitude;
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestView = view;
                }
            }

            return closestView;
        }
    }

    [HarmonyPatch(typeof(PageView), nameof(PageView.UpdateCursor))]
    internal static class RadialCursorCancelIndicatorPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PageView __instance)
        {
            VoidEngineUI.SuppressCenterCancelIndicator(__instance);
        }
    }

    [HarmonyPatch(typeof(PageView), nameof(PageView.Render))]
    internal static class RadialRenderCancelIndicatorPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PageView __instance)
        {
            VoidEngineUI.SuppressCenterCancelIndicator(__instance);
        }
    }
}




