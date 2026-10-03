using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSLZ.Bonelab;
using Il2CppSLZ.Marrow.Warehouse;
using Il2CppSLZ.UI;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace VoidEngine
{
    internal static class SpawnerSearch
    {
        private const int PageSize = 12;
        private const string SearchTabObjectName = "button_tab_voidworks_search";
        private const string SourceTabButtonPath = "group_tabs/grid_tabs/button_tab_05";
        private const string SourceItemButtonPath = "group_spawnSelect/section_SpawnablesList/grid_buttons/button_item_01";
        private static readonly Vector2 KeySize = new(80f, 80f);
        private static readonly Vector2 KeySpacing = new(10f, 10f);
        private static readonly Dictionary<int, PanelState> Panels = new();
        private static readonly Dictionary<string, SpawnableCrate> Spawnables = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SpawnableSearchEntry> SearchIndex = new(StringComparer.OrdinalIgnoreCase);
        private static List<SpawnableCrate> SortedSpawnables = new();
        private static readonly List<int> StalePanelIds = new();
        private static bool _searchIndexDirty = true;
        private static bool _enabled;
        private static bool _missingSearchThingLogged;
        private static bool _tabSetupWarningLogged;

        private sealed class PanelState
        {
            public SpawnablesPanelView Panel = null!;
            public Button? SearchTabButton;
            public int SearchTabIndex;
            public GameObject? KeyboardRoot;
            public string Query = string.Empty;
            public List<SpawnableCrate> Results = new();
            public int Page;
            public int SelectedItem = -1;
            public bool IsActive;
            public List<ItemButtonSnapshot>? NativeButtonSnapshot;
            public string? NativeLabel;
            public string? NativePageLabel;
            public SpawnableCrate? NativeSelectedObject;
            public string? NativeSelectedTitle;
            public string? NativeSelectedPallet;
            public string? NativeSelectedAuthor;
            public string? NativeSelectedDescription;
            public bool NativeNextPageActive;
            public bool NativePrevPageActive;
            public int RefreshAfterActivationFrames;
            public int ShowTabAfterFrames;
            public bool TabReady;
        }

        private sealed class SpawnableSearchEntry
        {
            public SpawnableCrate Crate = null!;
            public string Name = string.Empty;
            public string Barcode = string.Empty;
            public string Pallet = string.Empty;
            public string Author = string.Empty;
            public string Description = string.Empty;
        }

        private sealed class ItemButtonSnapshot
        {
            public string Text = string.Empty;
            public bool Active;
            public bool HighlightEnabled;
            public Color HighlightColor;
            public bool IconEnabled;
            public Sprite? IconSprite;
            public Color IconColor;
        }

        internal static void AddPallet(Pallet pallet)
        {
            if (pallet == null || pallet._crates == null || pallet._crates._items == null)
                return;

            try
            {
                foreach (Crate crate in pallet._crates._items)
                {
                    if (crate == null)
                        continue;

                    if (crate.TryCast<AvatarCrate>() != null || crate.TryCast<LevelCrate>() != null)
                        continue;

                    SpawnableCrate? spawnable = crate.TryCast<SpawnableCrate>();
                    if (spawnable == null || spawnable.Barcode == null)
                        continue;

                    string? barcode = spawnable.Barcode.ID;
                    if (!string.IsNullOrWhiteSpace(barcode))
                    {
                        Spawnables[barcode] = spawnable;
                        SearchIndex[barcode] = CreateSearchEntry(spawnable);
                        _searchIndexDirty = true;
                    }
                }

                foreach (PanelState state in Panels.Values)
                {
                    if (_enabled && state.IsActive)
                        RefreshResults(state);
                }
            }
            catch (Exception ex)
            {
                WarnSetupOnce($"Could not index a spawnable pallet: {ex.GetBaseException().Message}");
            }
        }

        internal static void Attach(SpawnablesPanelView panel)
        {
            if (panel == null || IsSearchThingLoaded())
                return;

            PruneDestroyedPanels();

            try
            {
                int id = panel.GetInstanceID();
                if (Panels.TryGetValue(id, out PanelState? existing))
                {
                    existing.Panel = panel;
                    if (_enabled && !existing.TabReady)
                    {
                        existing.SearchTabButton?.gameObject.SetActive(false);
                        if (existing.ShowTabAfterFrames <= 0)
                            existing.ShowTabAfterFrames = 1;
                    }
                    else
                    {
                        existing.SearchTabButton?.gameObject.SetActive(_enabled);
                    }
                    if (existing.IsActive)
                    {
                        existing.RefreshAfterActivationFrames = 2;
                        RefreshResults(existing);
                    }
                    return;
                }

                Transform? sourceTab = FindPalletsTab(panel) ?? panel.transform.Find(SourceTabButtonPath);
                Transform? sourceItem = panel.transform.Find(SourceItemButtonPath);
                if (sourceTab == null || sourceItem == null)
                {
                    WarnSetupOnce("Could not locate the spawn menu tab or item button used to build Search.");
                    return;
                }

                Button? tabButton = AddSearchTab(panel, sourceTab);
                ButtonReferenceHolder? itemStyle = sourceItem.GetComponent<ButtonReferenceHolder>();
                if (tabButton == null || itemStyle == null)
                {
                    WarnSetupOnce("Could not create the Search tab from BONELAB's spawn menu controls.");
                    return;
                }

                PanelState state = new()
                {
                    Panel = panel,
                    SearchTabButton = tabButton,
                    SearchTabIndex = panel.tabButtons.Count - 1,
                    ShowTabAfterFrames = _enabled ? 1 : 0
                };
                tabButton.gameObject.SetActive(false);
                Panels[id] = state;
                SubmenuPanelStyler.EnsureSpawnablesPanelStyled(panel.transform);
                Image? sourceIcon = FindTabIcon(sourceTab);
                MelonLogger.Msg(
                    $"[Void Engine] Void Search tab cloned from Pallets template '{sourceTab.name}' " +
                    $"(label='{GetTabLabel(sourceTab)}', icon='{(sourceIcon?.sprite != null ? sourceIcon.sprite.name : "none")}'); " +
                    $"tab will appear after setup. Indexed {Spawnables.Count} spawnables."
                );
            }
            catch (Exception ex)
            {
                WarnSetupOnce($"Spawner search setup failed: {ex.GetBaseException().Message}");
            }
        }

        internal static void SetEnabled(bool enabled)
        {
            _enabled = enabled;

            foreach (PanelState state in Panels.Values.ToArray())
            {
                if (!enabled && state.IsActive)
                {
                    try
                    {
                        if (state.Panel.tabButtons != null && state.Panel.tabButtons.Count > 0)
                            state.Panel.SelectTab(0);
                        else
                            RestoreNativePanel(state);
                    }
                    catch
                    {
                        RestoreNativePanel(state);
                        state.IsActive = false;
                    }
                }

                if (state.SearchTabButton != null)
                {
                    state.SearchTabButton.gameObject.SetActive(false);
                    state.ShowTabAfterFrames = enabled ? 1 : 0;
                    state.TabReady = false;
                }
                if (state.KeyboardRoot != null)
                    state.KeyboardRoot.SetActive(enabled && state.IsActive);

                if (enabled && state.IsActive)
                {
                    state.RefreshAfterActivationFrames = 2;
                    RefreshResults(state);
                }
            }
        }

        internal static bool BeforeSelectTab(SpawnablesPanelView panel, int index)
        {
            if (!TryGetState(panel, out PanelState state))
                return true;

            if (index == state.SearchTabIndex && !_enabled)
                return false;

            if (!state.IsActive || index == state.SearchTabIndex)
                return true;

            RestoreNativePanel(state);
            state.IsActive = false;
            if (state.KeyboardRoot != null)
                state.KeyboardRoot.SetActive(false);
            return true;
        }

        internal static void Update()
        {
            foreach (PanelState state in Panels.Values)
            {
                if (state.ShowTabAfterFrames > 0)
                {
                    state.ShowTabAfterFrames--;
                    if (state.ShowTabAfterFrames == 0 && _enabled && state.SearchTabButton != null)
                    {
                        state.SearchTabButton.gameObject.SetActive(true);
                        state.TabReady = true;
                    }
                }

                if (state.RefreshAfterActivationFrames <= 0)
                    continue;

                state.RefreshAfterActivationFrames--;
                if (state.RefreshAfterActivationFrames == 0 && state.IsActive)
                    RefreshResults(state);
            }
        }

        internal static void OnLevelLoaded()
        {
            PruneDestroyedPanels();
        }

        private static void PruneDestroyedPanels()
        {
            StalePanelIds.Clear();
            foreach (KeyValuePair<int, PanelState> pair in Panels)
            {
                if (pair.Value.Panel == null)
                    StalePanelIds.Add(pair.Key);
            }

            foreach (int id in StalePanelIds)
                Panels.Remove(id);
        }

        internal static void AfterSelectTab(SpawnablesPanelView panel, int index)
        {
            if (!TryGetState(panel, out PanelState state))
                return;

            state.IsActive = _enabled && index == state.SearchTabIndex;
            if (!state.IsActive)
            {
                if (index == state.SearchTabIndex)
                    RestoreNativePanel(state);
                if (state.KeyboardRoot != null)
                    state.KeyboardRoot.SetActive(false);
                return;
            }

            CaptureNativePanel(state);
            if (state.KeyboardRoot == null)
            {
                Transform? sourceItem = panel.transform.Find(SourceItemButtonPath);
                ButtonReferenceHolder? style = sourceItem != null
                    ? sourceItem.GetComponent<ButtonReferenceHolder>()
                    : null;
                if (style != null)
                {
                    state.KeyboardRoot = CreateKeyboard(panel, style, state);
                    if (state.KeyboardRoot != null)
                        SubmenuPanelStyler.StyleNewElementTree(state.KeyboardRoot.transform);
                }

                if (state.KeyboardRoot == null)
                    WarnSetupOnce("Could not create the VR keyboard for the spawner search tab.");
            }

            if (state.KeyboardRoot != null)
                state.KeyboardRoot.SetActive(true);
            RefreshResults(state);
        }

        internal static bool TryHandlePage(SpawnablesPanelView panel, int offset)
        {
            if (!_enabled || !TryGetState(panel, out PanelState state) || !state.IsActive)
                return false;

            int pageCount = GetPageCount(state);
            int nextPage = state.Page + offset;
            if (nextPage >= 0 && nextPage < pageCount)
            {
                state.Page = nextPage;
                state.SelectedItem = -1;
                Render(state);
            }

            return true;
        }

        internal static bool TryHandleItemSelection(SpawnablesPanelView panel, int buttonIndex)
        {
            if (!_enabled || !TryGetState(panel, out PanelState state) || !state.IsActive)
                return false;

            int resultIndex = state.Page * PageSize + buttonIndex;
            if (buttonIndex < 0 || buttonIndex >= PageSize || resultIndex >= state.Results.Count)
                return true;

            SpawnableCrate crate = state.Results[resultIndex];
            state.SelectedItem = resultIndex;
            if (panel.spawnGun != null)
                panel.spawnGun.OnSpawnableSelected(crate);
            UpdateSelectionDetails(panel, crate);
            Render(state);
            return true;
        }

        private static void UpdateSelectionDetails(SpawnablesPanelView panel, SpawnableCrate crate)
        {
            panel.selectedObject = crate;

            if (panel.selectedTitle != null)
                panel.selectedTitle.text = crate.name;
            if (panel.selectedPallet != null)
                panel.selectedPallet.text = crate._pallet != null ? crate._pallet.name : string.Empty;
            if (panel.selectedAuthor != null)
                panel.selectedAuthor.text = crate._pallet != null ? crate._pallet.Author : string.Empty;
            if (panel.selectedDescription != null)
                panel.selectedDescription.text = crate._description ?? string.Empty;
        }

        private static Button? AddSearchTab(SpawnablesPanelView panel, Transform sourceTab)
        {
            Transform? existing = sourceTab.parent != null
                ? sourceTab.parent.Find(SearchTabObjectName)
                : null;
            if (existing != null)
            {
                Transform? existingLabelTransform = existing.Find("text_spawnable_val");
                TextMeshPro? existingLabel = existingLabelTransform != null
                    ? existingLabelTransform.GetComponent<TextMeshPro>()
                    : null;
                ConfigureSearchTabLabel(existingLabel);

                Button? existingButton = existing.GetComponent<Button>();
                if (existingButton != null)
                    existingButton.gameObject.SetActive(false);
                return existingButton;
            }

            GameObject tabObject = UnityEngine.Object.Instantiate(sourceTab.gameObject, sourceTab.parent);
            tabObject.SetActive(false);
            tabObject.name = SearchTabObjectName;
            SubmenuPanelStyler.CopyOriginalColorsToClone(sourceTab, tabObject.transform);
            Button? button = tabObject.GetComponent<Button>();
            ButtonReferenceHolder? holder = tabObject.GetComponent<ButtonReferenceHolder>();
            if (button == null || holder == null)
            {
                UnityEngine.Object.Destroy(tabObject);
                return null;
            }

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener((UnityAction)(() =>
            {
                if (_enabled)
                    panel.SelectTab(panel.tabButtons.Count - 1);
            }));

            Transform? labelTransform = tabObject.transform.Find("text_spawnable_val");
            TextMeshPro? label = labelTransform != null ? labelTransform.GetComponent<TextMeshPro>() : null;
            ConfigureSearchTabLabel(label);

            SubmenuPanelStyler.StyleNewSpawnablesElement(tabObject.transform);

            List<ButtonReferenceHolder> tabs = panel.tabButtons.ToList();
            tabs.Add(holder);
            panel.tabButtons = new Il2CppReferenceArray<ButtonReferenceHolder>(tabs.ToArray());
            return button;
        }

        private static Transform? FindPalletsTab(SpawnablesPanelView panel)
        {
            Transform? tabsRoot = panel.transform.Find("group_tabs/grid_tabs");
            if (tabsRoot == null)
                return null;

            for (int i = 0; i < tabsRoot.childCount; i++)
            {
                Transform candidate = tabsRoot.GetChild(i);
                if (candidate.name == SearchTabObjectName || candidate.GetComponent<Button>() == null)
                    continue;

                foreach (TMP_Text label in candidate.GetComponentsInChildren<TMP_Text>(true))
                {
                    string text = label.text?.Trim() ?? string.Empty;
                    if (text.Contains("pallet", StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }

            return tabsRoot.Find("button_tab_01");
        }

        private static string GetTabLabel(Transform tab)
        {
            foreach (TMP_Text label in tab.GetComponentsInChildren<TMP_Text>(true))
            {
                string text = label.text?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            return string.Empty;
        }

        private static Image? FindTabIcon(Transform tab)
        {
            foreach (Image image in tab.GetComponentsInChildren<Image>(true))
            {
                if (image != null && string.Equals(image.gameObject.name, "img_sprite", StringComparison.OrdinalIgnoreCase))
                    return image;
            }

            return null;
        }

        private static void ConfigureSearchTabLabel(TextMeshPro? label)
        {
            if (label == null)
                return;

            label.text = "Void Search";
        }

        private static GameObject? CreateKeyboard(
            SpawnablesPanelView panel,
            ButtonReferenceHolder style,
            PanelState state)
        {
            GameObject root = new("VoidworksSpawnerSearchKeyboard");
            root.transform.SetParent(panel.transform, false);
            root.layer = 5;
            root.AddComponent<CanvasRenderer>();
            Image background = root.AddComponent<Image>();
            background.color = Color.clear;
            background.raycastTarget = false;

            RectTransform? rootRect = root.GetComponent<RectTransform>();
            if (rootRect == null)
            {
                UnityEngine.Object.Destroy(root);
                return null;
            }

            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.offsetMin = new Vector2(10f, 10f);
            rootRect.offsetMax = new Vector2(-10f, -10f);
            root.transform.localPosition = new Vector3(-85f, -450f, 0f);

            string[] rows = { "1234567890", "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
            float y = 0f;
            foreach (string row in rows)
            {
                float rowWidth = row.Length * KeySize.x + (row.Length - 1) * KeySpacing.x;
                float startX = -rowWidth / 2f;
                for (int i = 0; i < row.Length; i++)
                {
                    char character = row[i];
                    Vector2 position = new(
                        startX + i * (KeySize.x + KeySpacing.x) + KeySize.x / 2f,
                        y
                    );
                    CreateKey(root.transform, style, character.ToString(), position, KeySize,
                        () => ChangeQuery(state, state.Query + character));
                }

                y -= KeySize.y + KeySpacing.y;
            }

            CreateKey(root.transform, style, "Backspace", new Vector2(-280f, y), new Vector2(240f, 80f),
                () => ChangeQuery(state, state.Query.Length == 0 ? string.Empty : state.Query[..^1]));
            CreateKey(root.transform, style, "Space", new Vector2(0f, y), new Vector2(249f, 80f),
                () => ChangeQuery(state, state.Query + " "));
            CreateKey(root.transform, style, "Clear", new Vector2(280f, y), new Vector2(240f, 80f),
                () => ChangeQuery(state, string.Empty));

            return root;
        }

        private static void CreateKey(
            Transform parent,
            ButtonReferenceHolder style,
            string text,
            Vector2 position,
            Vector2 size,
            Action onClick)
        {
            GameObject key = new($"VoidworksSearchKey_{text}");
            key.transform.SetParent(parent, false);
            RectTransform keyRect = key.AddComponent<RectTransform>();
            keyRect.anchorMin = new Vector2(0.5f, 0.5f);
            keyRect.anchorMax = new Vector2(0.5f, 0.5f);
            keyRect.pivot = new Vector2(0.5f, 0.5f);
            key.AddComponent<CanvasRenderer>();
            key.layer = 5;

            GameObject face = new("Background");
            face.transform.SetParent(key.transform, false);
            RectTransform faceRect = face.AddComponent<RectTransform>();
            faceRect.anchorMin = new Vector2(0.5f, 0.5f);
            faceRect.anchorMax = new Vector2(0.5f, 0.5f);
            faceRect.pivot = new Vector2(0.5f, 0.5f);
            faceRect.anchoredPosition = Vector2.zero;
            faceRect.sizeDelta = size;
            Image faceImage = face.AddComponent<Image>();
            faceImage.sprite = style.background.sprite;
            faceImage.type = Image.Type.Sliced;

            GameObject outline = new("Outline");
            outline.transform.SetParent(key.transform, false);
            RectTransform outlineRect = outline.AddComponent<RectTransform>();
            outlineRect.anchorMin = new Vector2(0.5f, 0.5f);
            outlineRect.anchorMax = new Vector2(0.5f, 0.5f);
            outlineRect.pivot = new Vector2(0.5f, 0.5f);
            outlineRect.anchoredPosition = Vector2.zero;
            outlineRect.sizeDelta = size;
            Image outlineImage = outline.AddComponent<Image>();
            outlineImage.sprite = style.highlight.sprite;
            outlineImage.type = Image.Type.Sliced;
            outlineImage.raycastTarget = false;
            outlineImage.fillCenter = false;

            GameObject colliderObject = new("Collider");
            colliderObject.transform.SetParent(key.transform, false);
            BoxCollider collider = colliderObject.AddComponent<BoxCollider>();
            collider.size = new Vector3(size.x + KeySpacing.x / 2f, size.y + KeySpacing.y / 2f, 1f);
            collider.isTrigger = true;
            colliderObject.layer = 5;

            Button button = key.AddComponent<Button>();
            button.onClick.AddListener((UnityAction)(() => onClick()));
            button.transition = Selectable.Transition.ColorTint;
            button.targetGraphic = faceImage;
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0f, 0f, 0f, 0.61f);
            colors.highlightedColor = new Color(0.6f, 0.6f, 0.6f, 0.21f);
            colors.pressedColor = new Color(0.72f, 0.72f, 0.72f, 1f);
            colors.selectedColor = colors.normalColor;
            button.colors = colors;
            key.AddComponent<ButtonHoverClick>();

            GameObject textObject = new("Text");
            textObject.transform.SetParent(key.transform, false);
            TextMeshProUGUI label = textObject.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.font = style.tmp.font;
            label.fontSize = text.Length > 2 ? 24f : 40f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;

            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            textRect.anchoredPosition = Vector2.zero;

            keyRect.sizeDelta = size;
            keyRect.anchoredPosition = position;
        }

        private static void ChangeQuery(PanelState state, string query)
        {
            state.Query = query;
            state.Page = 0;
            state.SelectedItem = -1;
            RefreshResults(state);
        }

        private static void RefreshResults(PanelState state)
        {
            string query = state.Query.Trim();
            string normalizedQuery = CatalogSearchText.Normalize(query);
            EnsureSearchIndex();

            if (normalizedQuery.Length == 0)
            {
                state.Results = new List<SpawnableCrate>(SortedSpawnables);
            }
            else
            {
                state.Results = SearchIndex.Values
                    .Where(entry => EntryMatches(entry, normalizedQuery))
                    .OrderBy(entry => Relevance(entry, normalizedQuery))
                    .ThenBy(entry => entry.Crate.name, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => entry.Crate)
                    .ToList();
            }

            int pageCount = GetPageCount(state);
            state.Page = Math.Clamp(state.Page, 0, pageCount - 1);
            if (state.IsActive)
                Render(state);
        }

        private static SpawnableSearchEntry CreateSearchEntry(SpawnableCrate crate)
        {
            string barcode = crate.Barcode != null ? crate.Barcode.ID : string.Empty;
            string pallet = crate._pallet != null ? crate._pallet.name : string.Empty;
            string author = crate._pallet != null ? crate._pallet.Author : string.Empty;
            return new SpawnableSearchEntry
            {
                Crate = crate,
                Name = CatalogSearchText.Normalize(crate.name),
                Barcode = CatalogSearchText.Normalize(barcode),
                Pallet = CatalogSearchText.Normalize(pallet),
                Author = CatalogSearchText.Normalize(author),
                Description = CatalogSearchText.Normalize(crate._description ?? string.Empty)
            };
        }

        private static void EnsureSearchIndex()
        {
            if (!_searchIndexDirty)
                return;

            SortedSpawnables = SearchIndex.Values
                .OrderBy(entry => entry.Crate.name, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.Crate)
                .ToList();
            _searchIndexDirty = false;
        }

        private static bool EntryMatches(SpawnableSearchEntry entry, string normalizedQuery)
        {
            return entry.Name.Contains(normalizedQuery, StringComparison.Ordinal) ||
                   entry.Barcode.Contains(normalizedQuery, StringComparison.Ordinal) ||
                   entry.Pallet.Contains(normalizedQuery, StringComparison.Ordinal) ||
                   entry.Author.Contains(normalizedQuery, StringComparison.Ordinal) ||
                   entry.Description.Contains(normalizedQuery, StringComparison.Ordinal);
        }

        private static int Relevance(SpawnableSearchEntry entry, string normalizedQuery)
        {
            if (string.Equals(entry.Name, normalizedQuery, StringComparison.Ordinal))
                return 0;
            if (entry.Name.StartsWith(normalizedQuery, StringComparison.Ordinal))
                return 1;
            if (entry.Name.Contains(normalizedQuery, StringComparison.Ordinal))
                return 2;

            return 3;
        }

        private static int GetPageCount(PanelState state)
        {
            return Math.Max(1, (state.Results.Count + PageSize - 1) / PageSize);
        }

        private static void Render(PanelState state)
        {
            SpawnablesPanelView panel = state.Panel;
            if (panel == null || !state.IsActive)
                return;

            panel.labelText.text = state.Query.Length == 0 ? "Search" : $"Search: {state.Query}";
            int start = state.Page * PageSize;
            int availableButtons = Math.Min(PageSize, panel.itemButtons.Count);
            for (int i = 0; i < availableButtons; i++)
            {
                ButtonReferenceHolder? itemButton = panel.itemButtons[i];
                if (itemButton == null)
                    continue;

                int resultIndex = start + i;
                bool hasResult = resultIndex < state.Results.Count;
                itemButton.gameObject.SetActive(hasResult);
                if (!hasResult)
                    continue;

                itemButton.tmp.text = state.Results[resultIndex].name;
                itemButton.special.enabled = false;
                itemButton.highlight.enabled = state.SelectedItem == resultIndex;
            }

            int pageCount = GetPageCount(state);
            panel.itemPageText.text = state.Results.Count == 0
                ? "0 items"
                : $"{state.Page + 1}/{pageCount}";
            panel.itemScrollDownButton.gameObject.SetActive(state.Page < pageCount - 1);
            panel.itemScrollUpButton.gameObject.SetActive(state.Page > 0);
        }

        private static void CaptureNativePanel(PanelState state)
        {
            SpawnablesPanelView panel = state.Panel;
            state.NativeButtonSnapshot = new List<ItemButtonSnapshot>(panel.itemButtons.Count);
            foreach (ButtonReferenceHolder? button in panel.itemButtons)
            {
                if (button == null)
                {
                    state.NativeButtonSnapshot.Add(new ItemButtonSnapshot());
                    continue;
                }

                state.NativeButtonSnapshot.Add(new ItemButtonSnapshot
                {
                    Text = button.tmp.text,
                    Active = button.gameObject.activeSelf,
                    HighlightEnabled = button.highlight.enabled,
                    HighlightColor = button.highlight.color,
                    IconEnabled = button.special.enabled,
                    IconSprite = button.special.sprite,
                    IconColor = button.special.color
                });
            }

            state.NativeLabel = panel.labelText.text;
            state.NativePageLabel = panel.itemPageText.text;
            state.NativeSelectedObject = panel.selectedObject;
            state.NativeSelectedTitle = panel.selectedTitle != null ? panel.selectedTitle.text : null;
            state.NativeSelectedPallet = panel.selectedPallet != null ? panel.selectedPallet.text : null;
            state.NativeSelectedAuthor = panel.selectedAuthor != null ? panel.selectedAuthor.text : null;
            state.NativeSelectedDescription = panel.selectedDescription != null
                ? panel.selectedDescription.text
                : null;
            state.NativeNextPageActive = panel.itemScrollDownButton.gameObject.activeSelf;
            state.NativePrevPageActive = panel.itemScrollUpButton.gameObject.activeSelf;
        }

        private static void RestoreNativePanel(PanelState state)
        {
            SpawnablesPanelView panel = state.Panel;
            if (state.NativeButtonSnapshot == null)
                return;

            int count = Math.Min(state.NativeButtonSnapshot.Count, panel.itemButtons.Count);
            for (int i = 0; i < count; i++)
            {
                ButtonReferenceHolder? button = panel.itemButtons[i];
                if (button == null)
                    continue;

                ItemButtonSnapshot snapshot = state.NativeButtonSnapshot[i];
                button.tmp.text = snapshot.Text;
                button.gameObject.SetActive(snapshot.Active);
                button.highlight.enabled = snapshot.HighlightEnabled;
                button.highlight.color = snapshot.HighlightColor;
                button.special.enabled = snapshot.IconEnabled;
                button.special.sprite = snapshot.IconSprite;
                button.special.color = snapshot.IconColor;
            }

            if (state.NativeLabel != null)
                panel.labelText.text = state.NativeLabel;
            if (state.NativePageLabel != null)
                panel.itemPageText.text = state.NativePageLabel;
            panel.selectedObject = state.NativeSelectedObject;
            if (state.NativeSelectedTitle != null && panel.selectedTitle != null)
                panel.selectedTitle.text = state.NativeSelectedTitle;
            if (state.NativeSelectedPallet != null && panel.selectedPallet != null)
                panel.selectedPallet.text = state.NativeSelectedPallet;
            if (state.NativeSelectedAuthor != null && panel.selectedAuthor != null)
                panel.selectedAuthor.text = state.NativeSelectedAuthor;
            if (state.NativeSelectedDescription != null && panel.selectedDescription != null)
                panel.selectedDescription.text = state.NativeSelectedDescription;
            panel.itemScrollDownButton.gameObject.SetActive(state.NativeNextPageActive);
            panel.itemScrollUpButton.gameObject.SetActive(state.NativePrevPageActive);
            state.NativeButtonSnapshot = null;
        }

        private static bool TryGetState(SpawnablesPanelView panel, out PanelState state)
        {
            if (panel != null && Panels.TryGetValue(panel.GetInstanceID(), out PanelState? found) && found != null)
            {
                state = found;
                return true;
            }

            state = null!;
            return false;
        }

        private static bool IsSearchThingLoaded()
        {
            bool loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Any(assembly => string.Equals(assembly.GetName().Name, "SearchThing", StringComparison.Ordinal));
            if (loaded && !_missingSearchThingLogged)
            {
                _missingSearchThingLogged = true;
                MelonLogger.Msg("[Void Engine] SearchThing is loaded; skipping a duplicate spawner search tab.");
            }

            return loaded;
        }

        private static void WarnSetupOnce(string message)
        {
            if (_tabSetupWarningLogged)
                return;

            _tabSetupWarningLogged = true;
            MelonLogger.Warning($"[Void Engine] {message}");
        }
    }

    [HarmonyPatch(typeof(AssetWarehouse), nameof(AssetWarehouse.AddPallet))]
    internal static class SpawnerSearchPalletPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Pallet pallet)
        {
            SpawnerSearch.AddPallet(pallet);
        }
    }

    [HarmonyPatch(typeof(SpawnablesPanelView), nameof(SpawnablesPanelView.Activate))]
    internal static class SpawnerSearchActivatePatch
    {
        [HarmonyPostfix]
        private static void Postfix(SpawnablesPanelView __instance)
        {
            SpawnerSearch.Attach(__instance);
        }
    }

    [HarmonyPatch(typeof(SpawnablesPanelView), nameof(SpawnablesPanelView.SelectTab))]
    internal static class SpawnerSearchTabPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(SpawnablesPanelView __instance, int idx)
        {
            return SpawnerSearch.BeforeSelectTab(__instance, idx);
        }

        [HarmonyPostfix]
        private static void Postfix(SpawnablesPanelView __instance, int idx)
        {
            SpawnerSearch.AfterSelectTab(__instance, idx);
        }
    }

    [HarmonyPatch(typeof(SpawnablesPanelView), nameof(SpawnablesPanelView.NextPage))]
    internal static class SpawnerSearchNextPagePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(SpawnablesPanelView __instance)
        {
            return !SpawnerSearch.TryHandlePage(__instance, 1);
        }
    }

    [HarmonyPatch(typeof(SpawnablesPanelView), nameof(SpawnablesPanelView.PrevPage))]
    internal static class SpawnerSearchPreviousPagePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(SpawnablesPanelView __instance)
        {
            return !SpawnerSearch.TryHandlePage(__instance, -1);
        }
    }

    [HarmonyPatch(typeof(SpawnablesPanelView), nameof(SpawnablesPanelView.SelectItem))]
    internal static class SpawnerSearchSelectItemPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(SpawnablesPanelView __instance, int idx)
        {
            return !SpawnerSearch.TryHandleItemSelection(__instance, idx);
        }
    }
}
