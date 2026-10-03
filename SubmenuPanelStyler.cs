using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppSLZ.Bonelab;
using Il2CppSLZ.UI;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VoidEngine
{
    internal static class SubmenuPanelStyler
    {
        private static readonly Color PanelBlue = Main.VoidworksBlue;
        private static readonly Color SelectionCardPurple = new(0.20f, 0.035f, 0.30f, 1f);
        private static readonly Color SelectionCardHoverPurple = new(0.31f, 0.06f, 0.44f, 1f);
        private static readonly Color SelectionCardPressedPurple = new(0.43f, 0.10f, 0.58f, 1f);
        private static readonly Color SelectionOutlinePurple = Main.VoidworksBluePurple;
        private static readonly Color SelectionTextPurple = Main.VoidworksBluePurple;
        private static readonly Dictionary<Component, Color> OriginalComponentColors = new();
        private static readonly Dictionary<Button, (ColorBlock Colors, Selectable.Transition Transition)> OriginalSelectionButtonStyles = new();
        private static readonly HashSet<int> StyledSpawnPanelIds = new();
        private static readonly HashSet<int> LoggedSelectionPanelIds = new();
        private static readonly List<(Transform Root, PanelKind Kind)> SelectionPanelRoots = new();
        private static bool _enabled;
        private static bool _hierarchyLoggingEnabled;
        private static bool _fusionSuppressed;
        private static float _nextFusionCheck;
        private static float _nextSelectionRefresh;

        private enum PanelKind
        {
            Spawnables,
            Avatar,
            Level,
            Settings,
            SettingsGraphics
        }

        internal static void SetEnabled(bool enabled)
        {
            if (_enabled == enabled)
            {
                if (enabled)
                    Refresh();
                else
                {
                    Restore();
                    StyledSpawnPanelIds.Clear();
                    LoggedSelectionPanelIds.Clear();
                    SelectionPanelRoots.Clear();
                }
                return;
            }

            _enabled = enabled;
            if (enabled)
                Refresh();
            else
            {
                Restore();
                StyledSpawnPanelIds.Clear();
                LoggedSelectionPanelIds.Clear();
                SelectionPanelRoots.Clear();
            }
        }

        internal static void SetHierarchyLoggingEnabled(bool enabled)
        {
            _hierarchyLoggingEnabled = enabled;
            LoggedSelectionPanelIds.Clear();
            if (enabled && _enabled)
                Refresh();
        }

        internal static void Refresh()
        {
            if (!_enabled)
                return;

            if (IsFusionSessionActive())
            {
                _fusionSuppressed = true;
                Restore();
                return;
            }

            _fusionSuppressed = false;

            try
            {
                int selectionPanelCount = 0;
                int selectionButtonCount = 0;
                int selectionHolderCount = 0;
                List<string> selectionRoots = new();
                SelectionPanelRoots.Clear();
                foreach (GameObject sceneObject in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
                {
                    if (sceneObject == null || IsBodyMallInterface(sceneObject.transform))
                        continue;

                    if (IsMultiplayerInterface(sceneObject.transform))
                        continue;

                    string objectName = sceneObject.name;
                    if (objectName.Contains("group_toolMenu", StringComparison.OrdinalIgnoreCase))
                    {
                        StyledSpawnPanelIds.Add(sceneObject.GetInstanceID());
                        TintTree(sceneObject.transform, PanelKind.Spawnables, isRoot: true);
                    }
                    else if (IsAvatarSelectorName(objectName) || IsLevelSelectorName(objectName))
                    {
                        bool isAvatarPanel = IsAvatarSelectorName(objectName);
                        selectionPanelCount++;
                        selectionButtonCount += sceneObject.GetComponentsInChildren<Button>(true).Length;
                        selectionHolderCount += sceneObject.GetComponentsInChildren<ButtonReferenceHolder>(true).Length;
                        selectionRoots.Add(
                            $"{objectName}@{sceneObject.scene.name}(active={sceneObject.activeInHierarchy})"
                        );
                        PanelKind selectionKind = isAvatarPanel ? PanelKind.Avatar : PanelKind.Level;
                        SelectionPanelRoots.Add((sceneObject.transform, selectionKind));
                        TintTree(sceneObject.transform, selectionKind, isRoot: true);
                        if (_hierarchyLoggingEnabled && isAvatarPanel &&
                            LoggedSelectionPanelIds.Add(sceneObject.GetInstanceID()))
                        {
                            DumpAvatarPanelHierarchy(sceneObject);
                        }
                    }
                    else if (objectName.Contains("panel_Preferences", StringComparison.OrdinalIgnoreCase))
                        TintTree(sceneObject.transform, PanelKind.Settings, isRoot: true);
                    else if (objectName.Contains("grid_Graphics", StringComparison.OrdinalIgnoreCase))
                        TintTree(sceneObject.transform, PanelKind.SettingsGraphics, isRoot: true);
                }

                MelonLoader.MelonLogger.Msg(
                    $"[Void Engine] Selection panel theme pass: panels={selectionPanelCount}, buttons={selectionButtonCount}, " +
                    $"holders={selectionHolderCount}, roots=[{string.Join(", ", selectionRoots)}]."
                );
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning(
                    $"[Void Engine] Could not recolor submenu panels: {ex.GetBaseException().Message}"
                );
            }
        }

        internal static void EnsureSpawnablesPanelStyled(Transform panelRoot)
        {
            if (!_enabled || panelRoot == null || IsMultiplayerInterface(panelRoot) || IsBodyMallInterface(panelRoot))
                return;

            int panelId = panelRoot.gameObject.GetInstanceID();
            if (StyledSpawnPanelIds.Contains(panelId))
                return;

            if (IsFusionSessionActive())
            {
                _fusionSuppressed = true;
                Restore();
                return;
            }

            StyledSpawnPanelIds.Add(panelId);
            TintTree(panelRoot, PanelKind.Spawnables, isRoot: true);
        }

        internal static void StyleNewElementTree(Transform root)
        {
            if (!_enabled || root == null || IsMultiplayerInterface(root) || IsBodyMallInterface(root) || IsFusionSessionActive())
                return;

            TintElement(root);
            StyleNewElementChildren(root);
        }

        internal static void StyleNewSpawnablesElement(Transform root)
        {
            if (!_enabled || root == null || IsMultiplayerInterface(root) || IsBodyMallInterface(root) || IsFusionSessionActive())
                return;

            TintElement(root);
            TintTree(root, PanelKind.Spawnables, isRoot: false);
        }

        private static void StyleNewElementChildren(Transform parent)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (IsMultiplayerInterface(child) || IsBodyMallInterface(child))
                    continue;

                TintElement(child);
                StyleNewElementChildren(child);
            }
        }

        internal static void CopyOriginalColorsToClone(Transform sourceRoot, Transform cloneRoot)
        {
            if (sourceRoot == null || cloneRoot == null)
                return;

            Graphic[] sourceGraphics = sourceRoot.GetComponentsInChildren<Graphic>(true);
            Graphic[] cloneGraphics = cloneRoot.GetComponentsInChildren<Graphic>(true);
            int count = Math.Min(sourceGraphics.Length, cloneGraphics.Length);
            for (int i = 0; i < count; i++)
            {
                Graphic source = sourceGraphics[i];
                Graphic clone = cloneGraphics[i];
                if (source == null || clone == null)
                    continue;

                Color original = OriginalComponentColors.TryGetValue(source, out Color stored)
                    ? stored
                    : source.color;

                if (OriginalComponentColors.ContainsKey(clone))
                    OriginalComponentColors[clone] = original;
                else
                    OriginalComponentColors.Add(clone, original);
                clone.color = original;
            }
        }

        internal static void Update()
        {
            if (!_enabled)
                return;

            if (Time.unscaledTime >= _nextFusionCheck)
            {
                _nextFusionCheck = Time.unscaledTime + 1f;
                bool fusionActive = IsFusionSessionActive();
                if (fusionActive && !_fusionSuppressed)
                {
                    _fusionSuppressed = true;
                    Restore();
                }
                else if (!fusionActive && _fusionSuppressed)
                {
                    _fusionSuppressed = false;
                    Refresh();
                }
            }

            if (_fusionSuppressed || Time.unscaledTime < _nextSelectionRefresh)
                return;

            _nextSelectionRefresh = Time.unscaledTime + 1f;
            foreach ((Transform panelRoot, PanelKind kind) in SelectionPanelRoots)
            {
                if (panelRoot == null || !panelRoot.gameObject.activeInHierarchy)
                    continue;

                TintTree(panelRoot, kind, isRoot: true);
            }
        }

        internal static bool IsFusionSessionActive()
        {
            try
            {
                Assembly? fusionAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(assembly => string.Equals(
                        assembly.GetName().Name,
                        "LabFusion",
                        StringComparison.Ordinal
                    ));

                if (fusionAssembly == null)
                    return false;

                return ReadStaticBoolean(fusionAssembly, "LabFusion.Network.NetworkInfo", "HasServer") ||
                       ReadStaticBoolean(fusionAssembly, "LabFusion.Network.NetworkInfo", "IsHost") ||
                       ReadStaticBoolean(fusionAssembly, "LabFusion.Network.NetworkInfo", "IsClient") ||
                       ReadStaticBoolean(fusionAssembly, "LabFusion.Scene.NetworkSceneManager", "IsLevelNetworked") ||
                       ReadStaticBoolean(fusionAssembly, "LabFusion.Network.NetworkLayerManager", "LoggedIn");
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadStaticBoolean(Assembly assembly, string typeName, string memberName)
        {
            try
            {
                Type? type = assembly.GetType(typeName, throwOnError: false);
                if (type == null)
                    return false;

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                PropertyInfo? property = type.GetProperty(memberName, flags);
                if (property?.PropertyType == typeof(bool) && property.GetValue(null) is bool propertyValue)
                    return propertyValue;

                FieldInfo? field = type.GetField(memberName, flags);
                return field?.FieldType == typeof(bool) && field.GetValue(null) is bool fieldValue && fieldValue;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsMultiplayerInterface(Transform transform)
        {
            for (Transform? current = transform; current != null; current = current.parent)
            {
                string name = current.name;
                if (name.Contains("Fusion", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Multiplayer", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (IsFusionLabeledControl(current))
                    return true;
            }

            return false;
        }

        private static bool IsFusionLabeledControl(Transform transform)
        {
            bool controlLike = transform.name.Contains("button", StringComparison.OrdinalIgnoreCase) ||
                               transform.name.Contains("tab", StringComparison.OrdinalIgnoreCase) ||
                               transform.GetComponent<Button>() != null ||
                               transform.GetComponent<PageItemView>() != null;
            if (!controlLike)
                return false;

            foreach (TMP_Text label in transform.GetComponentsInChildren<TMP_Text>(true))
            {
                if (label == null)
                    continue;

                string text = label.text?.Trim() ?? string.Empty;
                if (text.Contains("Fusion", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Multiplayer", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsBodyMallInterface(Transform? transform)
        {
            for (Transform? current = transform; current != null; current = current.parent)
            {
                string name = current.name;
                if (name.Contains("BodyMall", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Body Mall", StringComparison.OrdinalIgnoreCase))
                    return true;

                AvatarsPanelView? avatarPanel = current.GetComponent<AvatarsPanelView>();
                if (avatarPanel != null && avatarPanel.isBodyMall)
                    return true;

                BonelabLevelsPanelView? levelsPanel = current.GetComponent<BonelabLevelsPanelView>();
                if (levelsPanel != null && !levelsPanel.isRadialMenu &&
                    !levelsPanel.isModMenu && !levelsPanel.isSandboxMenu)
                {
                    return true;
                }
            }

            return false;
        }

        internal static void Restore()
        {
            foreach (KeyValuePair<Component, Color> entry in OriginalComponentColors)
            {
                try
                {
                    if (entry.Key is Graphic graphic && graphic != null)
                        graphic.color = entry.Value;
                    else if (entry.Key is TMP_Text text && text != null)
                        text.color = entry.Value;
                }
                catch
                {
                }
            }

            OriginalComponentColors.Clear();

            foreach (KeyValuePair<Button, (ColorBlock Colors, Selectable.Transition Transition)> entry in OriginalSelectionButtonStyles)
            {
                try
                {
                    if (entry.Key != null)
                    {
                        entry.Key.colors = entry.Value.Colors;
                        entry.Key.transition = entry.Value.Transition;
                    }
                }
                catch
                {
                }
            }
            OriginalSelectionButtonStyles.Clear();

            StyledSpawnPanelIds.Clear();
        }

        private static void TintTree(Transform parent, PanelKind kind, bool isRoot)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (IsMultiplayerInterface(child) || IsBodyMallInterface(child))
                    continue;

                if (ShouldSkip(kind, child, i, isRoot))
                    continue;

                if (child.name != "VoidworksSpawnerSearchKeyboard")
                {
                    if (kind == PanelKind.Level || kind == PanelKind.Avatar)
                        TintSelectionElement(child, kind);
                    else
                        TintElement(child);
                }

                TintTree(child, kind, isRoot: false);
            }
        }

        private static bool ShouldSkip(PanelKind kind, Transform child, int childIndex, bool isRoot)
        {
            if (isRoot && childIndex == 1 && kind == PanelKind.Settings)
            {
                return true;
            }

            if (kind == PanelKind.Spawnables)
            {
                return child.name == "Background" ||
                       (child.name == "image_backline" && child.parent != null &&
                        child.parent.gameObject.name == "group_selectedInfo") ||
                       (isRoot && childIndex == 3);
            }

            if (kind == PanelKind.Settings)
            {
                return child.name == "Viewport_Spectator" || child.name == "Viewport_Graphics" ||
                       child.name == "Viewport" || child.name == "Name" ||
                       child.name.Contains("BoneMenu", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static void TintSelectionElement(Transform element, PanelKind kind)
        {
            Button? button = element.GetComponent<Button>();
            ButtonReferenceHolder? holder = element.GetComponent<ButtonReferenceHolder>();
            Graphic? background = holder?.background ?? button?.targetGraphic;
            Graphic? ownGraphic = element.GetComponent<Graphic>();
            bool avatarCategoryOutline = kind == PanelKind.Avatar &&
                                         IsAvatarCategoryOption(element) &&
                                         holder?.background != null &&
                                         holder.background.name.Contains("backline", StringComparison.OrdinalIgnoreCase);

            if (background == null && IsCardBackgroundName(element.name))
                background = ownGraphic;

            if (background != null)
            {
                SetManagedGraphicColor(
                    background,
                    avatarCategoryOutline ? SelectionOutlinePurple : SelectionCardPurple
                );

                if (button != null)
                {
                    if (!OriginalSelectionButtonStyles.ContainsKey(button))
                        OriginalSelectionButtonStyles.Add(button, (button.colors, button.transition));

                    ColorBlock colors = button.colors;
                    colors.normalColor = SelectionCardPurple;
                    colors.highlightedColor = SelectionCardHoverPurple;
                    colors.pressedColor = SelectionCardPressedPurple;
                    colors.selectedColor = SelectionCardHoverPurple;
                    colors.disabledColor = new Color(SelectionCardPurple.r, SelectionCardPurple.g, SelectionCardPurple.b, 0.55f);
                    colors.colorMultiplier = 1f;
                    button.colors = colors;
                    button.transition = Selectable.Transition.ColorTint;
                }
            }

            if (button?.targetGraphic != null && button.targetGraphic != background)
                SetManagedGraphicColor(button.targetGraphic, SelectionCardPurple);

            if (ownGraphic != null &&
                (IsOutlineName(element.name) ||
                 IsLevelOptionOutline(element, ownGraphic, kind) ||
                 IsAvatarForwardControlAccent(element, kind)))
                SetManagedGraphicColor(ownGraphic, SelectionOutlinePurple);

            if (holder?.highlight != null)
                SetManagedGraphicColor(holder.highlight, SelectionOutlinePurple);

            TMP_Text? text = element.GetComponent<TMP_Text>();
            if (text != null)
                SetManagedGraphicColor(text, SelectionTextPurple);

            if (holder?.tmp != null)
                SetManagedGraphicColor(holder.tmp, SelectionTextPurple);
            if (holder?.specialTmp != null)
                SetManagedGraphicColor(holder.specialTmp, SelectionTextPurple);
        }

        private static void SetManagedGraphicColor(Graphic graphic, Color color)
        {
            if (graphic == null || IsBodyMallInterface(graphic.transform))
                return;

            if (!OriginalComponentColors.TryGetValue(graphic, out Color original))
            {
                original = graphic.color;
                OriginalComponentColors.Add(graphic, original);
            }

            if (ColorsMatch(graphic.color, original) || ColorsMatch(graphic.color, color))
                graphic.color = color;
        }

        private static bool ColorsMatch(Color left, Color right)
        {
            const float epsilon = 0.002f;
            return Mathf.Abs(left.r - right.r) < epsilon &&
                   Mathf.Abs(left.g - right.g) < epsilon &&
                   Mathf.Abs(left.b - right.b) < epsilon &&
                   Mathf.Abs(left.a - right.a) < epsilon;
        }

        private static bool IsCardBackgroundName(string name)
        {
            return string.Equals(name, "img_bg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "img_background", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "background", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAvatarCategoryOption(Transform element)
        {
            return element.parent != null &&
                   IsAvatarSelectorName(element.parent.name) &&
                   element.name.StartsWith("button_Item_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAvatarForwardControlAccent(Transform element, PanelKind kind)
        {
            if (kind != PanelKind.Avatar || element.parent == null)
                return false;

            if (string.Equals(element.parent.name, "button_Forward", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(element.name, "image_backline", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(element.name, "image_frontline", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(element.name, "img_arrow", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(element.parent.name, "button_next", StringComparison.OrdinalIgnoreCase);
        }

        private static void DumpAvatarPanelHierarchy(GameObject panelRoot)
        {
            MelonLoader.MelonLogger.Msg(
                $"[Void Engine] Avatar selector hierarchy probe: root={panelRoot.name}, " +
                $"active={panelRoot.activeInHierarchy}, scene={panelRoot.scene.name}"
            );

            foreach (Transform node in panelRoot.GetComponentsInChildren<Transform>(true))
            {
                Button? button = node.GetComponent<Button>();
                ButtonReferenceHolder? holder = node.GetComponent<ButtonReferenceHolder>();
                Graphic? graphic = node.GetComponent<Graphic>();
                TMP_Text? text = node.GetComponent<TMP_Text>();
                if (button == null && holder == null && graphic == null && text == null)
                    continue;

                string path = GetRelativePath(node, panelRoot.transform);
                string color = graphic != null ? FormatColor(graphic.color) : "none";
                string label = text != null ? TrimForLog(text.text) : string.Empty;
                MelonLoader.MelonLogger.Msg(
                    $"[Void Engine] Avatar selector node | {path} | active={node.gameObject.activeInHierarchy} | " +
                    $"graphic={graphic?.GetType().Name ?? "none"}:{color} | text='{label}' | " +
                    $"buttonTarget={button?.targetGraphic?.name ?? "none"} | " +
                    $"holderBackground={holder?.background?.name ?? "none"} | " +
                    $"holderHighlight={holder?.highlight?.name ?? "none"}"
                );
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

        private static string FormatColor(Color color)
        {
            return $"({color.r:F2},{color.g:F2},{color.b:F2},{color.a:F2})";
        }

        private static string TrimForLog(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string singleLine = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return singleLine.Length <= 60 ? singleLine : singleLine.Substring(0, 60);
        }

        private static bool IsOutlineName(string name)
        {
            return name.Contains("outline", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "highlight", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "img_highlight", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLevelOptionOutline(Transform element, Graphic graphic, PanelKind kind)
        {
            if (kind != PanelKind.Level)
                return false;

            string name = element.name;
            if (name.Contains("border", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("frame", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("stroke", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("edge", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            bool lineGraphic = name.Contains("backline", StringComparison.OrdinalIgnoreCase) ||
                               name.Contains("frontline", StringComparison.OrdinalIgnoreCase);
            if (!lineGraphic)
                return false;

            ButtonReferenceHolder? parentHolder = element.parent?.GetComponent<ButtonReferenceHolder>();
            return parentHolder == null || parentHolder.background != graphic;
        }

        internal static bool IsLevelSelectorName(string name)
        {
            return IsExactPanelRootName(name, "group_levelSelect");
        }

        internal static bool IsAvatarSelectorName(string name)
        {
            return IsExactPanelRootName(name, "group_AvatarSelect");
        }

        private static bool IsExactPanelRootName(string name, string expectedName)
        {
            return string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith(expectedName + " (", StringComparison.OrdinalIgnoreCase);
        }

        private static void TintElement(Transform element)
        {
            if (IsBodyMallInterface(element))
                return;

            Graphic? graphic = element.GetComponent<Graphic>();
            if (graphic != null)
            {
                Color original = graphic.color;
                if (original.a <= 0.001f)
                    return;

                if (!OriginalComponentColors.ContainsKey(graphic))
                    OriginalComponentColors.Add(graphic, original);

                Color tinted = PanelBlue;
                tinted.a = original.a;
                graphic.color = tinted;
                return;
            }

            TMP_Text? text = element.GetComponent<TMP_Text>();
            if (text != null)
            {
                Color original = text.color;
                if (original.a <= 0.001f)
                    return;

                if (!OriginalComponentColors.ContainsKey(text))
                    OriginalComponentColors.Add(text, original);

                Color tinted = PanelBlue;
                tinted.a = original.a;
                text.color = tinted;
            }
        }

    }
}
