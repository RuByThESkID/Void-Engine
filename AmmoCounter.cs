using BoneLib;
using BoneLib.BoneMenu;
using BoneLib.Notifications;
using Il2CppSLZ.Bonelab;
using Il2CppTMPro;
using MelonLoader;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace VoidEngine
{
    public static class AmmoCounter
    {
        private const string LeftAmmoReceiverPath = "PhysicsRig/Pelvis/BeltLf1/InventoryAmmoReceiver";
        private const string RightAmmoReceiverPath = "PhysicsRig/Pelvis/BeltRt1/InventoryAmmoReceiver";
        private const float RefreshInterval = 0.2f;
        private const float RetryInterval = 0.5f;
        private const string PreferenceCategoryId = "VoidInterface_AmmoCounter";
        private static readonly Vector3 RightBeltDefaultPosition = new(-0.0600f, 0.1100f, 0.0100f);
        private static readonly Vector3 RightBeltDefaultEulerAngles = new(81.0000f, 0.0000f, 13.0000f);
        private static readonly Vector3 LeftBeltDefaultPosition = new(0.0000f, 0.1000f, 0.0200f);
        private static readonly Vector3 LeftBeltDefaultEulerAngles = new(84.0000f, 23.0000f, 13.0000f);

        private enum PouchSide
        {
            Left = 0,
            Right = 1,
            Custom = 2
        }

        private static PouchSide _sidePreference = PouchSide.Left;
        private static PouchSide _activeSide = PouchSide.Left;
        private static PouchSide _customBaseSide = PouchSide.Left;
        private static bool _customPlacementInitialized;
        private static MelonPreferences_Category? _preferenceCategory;
        private static MelonPreferences_Entry<int>? _savedSidePreference;
        private static MelonPreferences_Entry<int>? _savedCustomBaseSide;
        private static MelonPreferences_Entry<bool>? _savedCustomPlacementInitialized;
        private static readonly PlacementPreferenceEntries LeftPlacementPreferences = new();
        private static readonly PlacementPreferenceEntries RightPlacementPreferences = new();
        private static readonly PlacementPreferenceEntries CustomPlacementPreferences = new();
        private static Vector3 _leftBeltPosition = LeftBeltDefaultPosition;
        private static Vector3 _leftBeltEulerAngles = LeftBeltDefaultEulerAngles;
        private static Vector3 _rightBeltPosition = RightBeltDefaultPosition;
        private static Vector3 _rightBeltEulerAngles = RightBeltDefaultEulerAngles;
        private static Vector3 _customPosition = LeftBeltDefaultPosition;
        private static Vector3 _customEulerAngles = LeftBeltDefaultEulerAngles;

        private static Vector3 _localPosition = RightBeltDefaultPosition;
        private static Vector3 _localEulerAngles = RightBeltDefaultEulerAngles;

        private static bool _enabled;
        private static bool _syncingPlacementControls;
        private static float _nextRefresh;
        private static float _nextRetry;
        private static float _nextCustomPlacementNotification;
        private static readonly HashSet<string> LoggedSetupWarnings = new(StringComparer.Ordinal);
        private static GameObject? _counterVisual;
        private static Transform? _ammoReceiver;
        private static UI_HUD? _sourceHud;
        private static TMP_Text? _sourceLight;
        private static TMP_Text? _sourceMedium;
        private static TMP_Text? _sourceHeavy;
        private static TMP_Text? _counterLight;
        private static TMP_Text? _counterMedium;
        private static TMP_Text? _counterHeavy;
        private static IntElement? _positionXControl;
        private static IntElement? _positionYControl;
        private static IntElement? _positionZControl;
        private static IntElement? _pitchControl;
        private static IntElement? _yawControl;
        private static IntElement? _rollControl;
        private static EnumElement? _pouchSideControl;
        private static BoneLib.BoneMenu.Page? _placementPage;

        private sealed class PlacementPreferenceEntries
        {
            private MelonPreferences_Entry<float>? _positionX;
            private MelonPreferences_Entry<float>? _positionY;
            private MelonPreferences_Entry<float>? _positionZ;
            private MelonPreferences_Entry<float>? _pitch;
            private MelonPreferences_Entry<float>? _yaw;
            private MelonPreferences_Entry<float>? _roll;

            public void Initialize(MelonPreferences_Category category, string prefix, Vector3 position, Vector3 eulerAngles)
            {
                _positionX = category.CreateEntry(prefix + "PositionX", position.x);
                _positionY = category.CreateEntry(prefix + "PositionY", position.y);
                _positionZ = category.CreateEntry(prefix + "PositionZ", position.z);
                _pitch = category.CreateEntry(prefix + "Pitch", eulerAngles.x);
                _yaw = category.CreateEntry(prefix + "Yaw", eulerAngles.y);
                _roll = category.CreateEntry(prefix + "Roll", eulerAngles.z);
            }

            public Vector3 ReadPosition(Vector3 fallback) => new(
                _positionX?.Value ?? fallback.x,
                _positionY?.Value ?? fallback.y,
                _positionZ?.Value ?? fallback.z
            );

            public Vector3 ReadEulerAngles(Vector3 fallback) => new(
                _pitch?.Value ?? fallback.x,
                _yaw?.Value ?? fallback.y,
                _roll?.Value ?? fallback.z
            );

            public void Write(Vector3 position, Vector3 eulerAngles)
            {
                if (_positionX != null) _positionX.Value = position.x;
                if (_positionY != null) _positionY.Value = position.y;
                if (_positionZ != null) _positionZ.Value = position.z;
                if (_pitch != null) _pitch.Value = eulerAngles.x;
                if (_yaw != null) _yaw.Value = eulerAngles.y;
                if (_roll != null) _roll.Value = eulerAngles.z;
            }
        }

        public static void AddPlacementControls(BoneLib.BoneMenu.Page page)
        {
            _placementPage = page;
            _positionXControl = page.CreateInt("Offset X (cm)", Main.VoidworksPurple,
                Mathf.RoundToInt(_localPosition.x * 100f), 1, -50, 50,
                value => SetPositionAxis(0, value));
            _positionYControl = page.CreateInt("Offset Y (cm)", Main.VoidworksPurple,
                Mathf.RoundToInt(_localPosition.y * 100f), 1, -50, 50,
                value => SetPositionAxis(1, value));
            _positionZControl = page.CreateInt("Offset Z (cm)", Main.VoidworksPurple,
                Mathf.RoundToInt(_localPosition.z * 100f), 1, -50, 50,
                value => SetPositionAxis(2, value));
            _pitchControl = page.CreateInt("Pitch (X deg)", Main.VoidworksPurple,
                Mathf.RoundToInt(_localEulerAngles.x), 1, -180, 180,
                value => SetRotationAxis(0, value));
            _yawControl = page.CreateInt("Yaw (Y deg)", Main.VoidworksPurple,
                Mathf.RoundToInt(_localEulerAngles.y), 1, -180, 180,
                value => SetRotationAxis(1, value));
            _rollControl = page.CreateInt("Roll (Z deg)", Main.VoidworksPurple,
                Mathf.RoundToInt(_localEulerAngles.z), 1, -180, 180,
                value => SetRotationAxis(2, value));
            page.CreateFunction("Reset Placement", Main.VoidworksPurple, ResetPlacement);
            ConfigurePlacementControlRanges();
        }

        public static void AddPlacementLoggerControl(BoneLib.BoneMenu.Page page)
        {
            page.CreateFunction("Log Ammo Counter Placement", Main.VoidworksPurple, LogPlacement);
        }

        public static void AddPouchSideControl(BoneLib.BoneMenu.Page page)
        {
            _pouchSideControl = page.CreateEnum(
                "Ammo Counter Position",
                Main.VoidworksPurple,
                (Enum)_sidePreference,
                SetPouchSidePreference
            );
        }

        public static void Initialize()
        {
            InitializePreferences();
            Hooking.OnUIRigCreated += OnUIRigCreated;
            Hooking.OnLevelLoaded += _ => OnLevelLoaded();
            MelonLogger.Msg("[Void Engine] Visual-only ammo counter hooks registered.");
        }

        private static void InitializePreferences()
        {
            try
            {
                _preferenceCategory = MelonPreferences.CreateCategory(
                    PreferenceCategoryId,
                    "Void Engine Ammo Counter"
                );
                _savedSidePreference = _preferenceCategory.CreateEntry(
                    "PouchSide",
                    (int)PouchSide.Left,
                    "Ammo Counter Position",
                    "0 = Left, 1 = Right, 2 = Custom"
                );
                _savedCustomBaseSide = _preferenceCategory.CreateEntry(
                    "CustomBaseSide",
                    (int)PouchSide.Left,
                    "Custom Placement Base Side",
                    "0 = Left, 1 = Right"
                );
                _savedCustomPlacementInitialized = _preferenceCategory.CreateEntry(
                    "CustomPlacementInitialized",
                    false
                );
                LeftPlacementPreferences.Initialize(
                    _preferenceCategory,
                    "Left",
                    LeftBeltDefaultPosition,
                    LeftBeltDefaultEulerAngles
                );
                RightPlacementPreferences.Initialize(
                    _preferenceCategory,
                    "Right",
                    RightBeltDefaultPosition,
                    RightBeltDefaultEulerAngles
                );
                CustomPlacementPreferences.Initialize(
                    _preferenceCategory,
                    "Custom",
                    LeftBeltDefaultPosition,
                    LeftBeltDefaultEulerAngles
                );

                int savedMode = _savedSidePreference.Value;
                _sidePreference = savedMode switch
                {
                    (int)PouchSide.Right => PouchSide.Right,
                    (int)PouchSide.Custom => PouchSide.Custom,
                    _ => PouchSide.Left
                };
                _customBaseSide = _savedCustomBaseSide.Value == (int)PouchSide.Right
                    ? PouchSide.Right
                    : PouchSide.Left;
                _customPlacementInitialized = _savedCustomPlacementInitialized.Value;
                _leftBeltPosition = LeftPlacementPreferences.ReadPosition(LeftBeltDefaultPosition);
                _leftBeltEulerAngles = LeftPlacementPreferences.ReadEulerAngles(LeftBeltDefaultEulerAngles);
                _rightBeltPosition = RightPlacementPreferences.ReadPosition(RightBeltDefaultPosition);
                _rightBeltEulerAngles = RightPlacementPreferences.ReadEulerAngles(RightBeltDefaultEulerAngles);
                _customPosition = CustomPlacementPreferences.ReadPosition(LeftBeltDefaultPosition);
                _customEulerAngles = CustomPlacementPreferences.ReadEulerAngles(LeftBeltDefaultEulerAngles);

                if (_sidePreference == PouchSide.Custom && !_customPlacementInitialized)
                {
                    CopySidePlacementToCustom(_customBaseSide);
                    _customPlacementInitialized = true;
                    SavePreferences();
                }

                LoadProfile(_sidePreference);

                MelonLogger.Msg($"[Void Engine] Loaded saved ammo counter settings; pouch side={_sidePreference}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Void Engine] Could not load saved ammo counter settings: {ex.Message}");
                LoadProfile(_sidePreference);
            }
        }

        private static void SavePreferences()
        {
            if (_preferenceCategory == null || _savedSidePreference == null ||
                _savedCustomBaseSide == null || _savedCustomPlacementInitialized == null)
                return;

            try
            {
                _savedSidePreference.Value = (int)_sidePreference;
                _savedCustomBaseSide.Value = (int)_customBaseSide;
                _savedCustomPlacementInitialized.Value = _customPlacementInitialized;
                LeftPlacementPreferences.Write(_leftBeltPosition, _leftBeltEulerAngles);
                RightPlacementPreferences.Write(_rightBeltPosition, _rightBeltEulerAngles);
                CustomPlacementPreferences.Write(_customPosition, _customEulerAngles);
                MelonPreferences.Save();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Void Engine] Could not save ammo counter settings: {ex.Message}");
            }
        }

        private static void SetPositionAxis(int axis, int centimeters)
        {
            if (_syncingPlacementControls)
                return;

            if (_activeSide != PouchSide.Custom)
            {
                ShowCustomRequiredNotification();
                SyncPlacementControls();
                return;
            }

            float meters = centimeters / 100f;
            if (axis == 0) _localPosition.x = meters;
            else if (axis == 1) _localPosition.y = meters;
            else _localPosition.z = meters;

            StoreActiveProfile();
            SavePreferences();
            ApplyPlacement();
        }

        private static void SetRotationAxis(int axis, int degrees)
        {
            if (_syncingPlacementControls)
                return;

            if (_activeSide != PouchSide.Custom)
            {
                ShowCustomRequiredNotification();
                SyncPlacementControls();
                return;
            }

            float angle = degrees;
            if (axis == 0) _localEulerAngles.x = angle;
            else if (axis == 1) _localEulerAngles.y = angle;
            else _localEulerAngles.z = angle;

            StoreActiveProfile();
            SavePreferences();
            ApplyPlacement();
        }

        private static void SetPouchSidePreference(Enum value)
        {
            if (value is not PouchSide side || side == _sidePreference)
                return;

            PouchSide previousSide = _sidePreference == PouchSide.Custom
                ? _customBaseSide
                : _sidePreference;

            if (side == PouchSide.Custom)
            {
                _customBaseSide = previousSide;
                if (!_customPlacementInitialized)
                {
                    CopySidePlacementToCustom(_customBaseSide);
                    _customPlacementInitialized = true;
                }
            }
            else
            {
                _customBaseSide = side;
            }

            _sidePreference = side;
            LoggedSetupWarnings.Clear();
            SavePreferences();
            DestroyCounter();
            LoadProfile(side);
            _nextRetry = 0f;

            if (side == PouchSide.Custom)
                ShowCustomPlacementNotification();

            if (_enabled)
                TryCreateCounter();
            else
                FindAmmoReceiver();

            MelonLogger.Msg($"[Void Engine] Ammo counter position set to {side}.");
        }

        private static void ShowCustomPlacementNotification()
        {
            try
            {
                Notifier.Send(new Notification
                {
                    Title = new NotificationText("Voidworks", Main.VoidworksPurple, true),
                    Message = new NotificationText(
                        "Go to advanced settings for placement controls.",
                        Color.white,
                        true
                    ),
                    ShowTitleOnPopup = true,
                    PopupLength = 4f,
                    Type = NotificationType.Information
                });
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Void Engine] Could not show the Custom placement notification: {ex.Message}");
            }
        }

        private static void ShowCustomRequiredNotification()
        {
            if (Time.unscaledTime < _nextCustomPlacementNotification)
                return;

            _nextCustomPlacementNotification = Time.unscaledTime + 2.5f;
            try
            {
                Notifier.Send(new Notification
                {
                    Title = new NotificationText("Voidworks", Main.VoidworksPurple, true),
                    Message = new NotificationText(
                        "Change Ammo Counter position to \"Custom\"",
                        Color.white,
                        true
                    ),
                    ShowTitleOnPopup = true,
                    PopupLength = 4f,
                    Type = NotificationType.Information
                });
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Void Engine] Could not show the Custom-required notification: {ex.Message}");
            }
        }

        private static void CopySidePlacementToCustom(PouchSide side)
        {
            if (side == PouchSide.Right)
            {
                _customPosition = _rightBeltPosition;
                _customEulerAngles = _rightBeltEulerAngles;
            }
            else
            {
                _customPosition = _leftBeltPosition;
                _customEulerAngles = _leftBeltEulerAngles;
            }
        }

        private static void StoreActiveProfile()
        {
            if (_activeSide == PouchSide.Custom)
            {
                _customPosition = _localPosition;
                _customEulerAngles = _localEulerAngles;
            }
            else if (_activeSide == PouchSide.Left)
            {
                _leftBeltPosition = _localPosition;
                _leftBeltEulerAngles = _localEulerAngles;
            }
            else
            {
                _rightBeltPosition = _localPosition;
                _rightBeltEulerAngles = _localEulerAngles;
            }
        }

        private static void LoadProfile(PouchSide side)
        {
            if (side == PouchSide.Left)
            {
                _localPosition = _leftBeltPosition;
                _localEulerAngles = _leftBeltEulerAngles;
            }
            else if (side == PouchSide.Right)
            {
                _localPosition = _rightBeltPosition;
                _localEulerAngles = _rightBeltEulerAngles;
            }
            else
            {
                _localPosition = _customPosition;
                _localEulerAngles = _customEulerAngles;
            }

            _activeSide = side;
            SyncPlacementControls();
            ConfigurePlacementControlRanges();
            ApplyPlacement();
            MelonLogger.Msg($"[Void Engine] Loaded {side.ToString().ToLowerInvariant()} ammo counter placement.");
        }

        private static void SyncPlacementControls()
        {
            _syncingPlacementControls = true;
            try
            {
                SetControlValue(_positionXControl, Mathf.RoundToInt(_localPosition.x * 100f));
                SetControlValue(_positionYControl, Mathf.RoundToInt(_localPosition.y * 100f));
                SetControlValue(_positionZControl, Mathf.RoundToInt(_localPosition.z * 100f));
                SetControlValue(_pitchControl, Mathf.RoundToInt(_localEulerAngles.x));
                SetControlValue(_yawControl, Mathf.RoundToInt(_localEulerAngles.y));
                SetControlValue(_rollControl, Mathf.RoundToInt(_localEulerAngles.z));
            }
            finally
            {
                _syncingPlacementControls = false;
            }
        }

        private static void ConfigurePlacementControlRanges()
        {
            SetPlacementControlRange(_positionXControl, -50, 50);
            SetPlacementControlRange(_positionYControl, -50, 50);
            SetPlacementControlRange(_positionZControl, -50, 50);
            SetPlacementControlRange(_pitchControl, -180, 180);
            SetPlacementControlRange(_yawControl, -180, 180);
            SetPlacementControlRange(_rollControl, -180, 180);
        }

        private static void SetPlacementControlRange(IntElement? control, int minimum, int maximum)
        {
            if (control == null)
                return;

            control.MinValue = minimum;
            control.MaxValue = maximum;
        }

        private static void ApplyPlacement()
        {
            if (_counterVisual == null)
                return;

            _counterVisual.transform.localPosition = _localPosition;
            _counterVisual.transform.localEulerAngles = _localEulerAngles;
        }

        private static void LogPlacement()
        {
            static string Format(float value) => value.ToString("0.0000", CultureInfo.InvariantCulture) + "f";
            string receiverLabel = _ammoReceiver == null
                ? "unbound"
                : $"{_ammoReceiver.parent?.name ?? "root"}/{_ammoReceiver.name}";

            MelonLogger.Msg(
                $"[Void Engine] Ammo counter placement | pouchSide={_activeSide} | " +
                $"receiver={receiverLabel} | localPosition = new Vector3(" +
                $"{Format(_localPosition.x)}, {Format(_localPosition.y)}, {Format(_localPosition.z)}) | " +
                "localEulerAngles = new Vector3(" +
                $"{Format(_localEulerAngles.x)}, {Format(_localEulerAngles.y)}, {Format(_localEulerAngles.z)})"
            );
        }

        private static void ResetPlacement()
        {
            if (_activeSide != PouchSide.Custom)
            {
                ShowCustomRequiredNotification();
                return;
            }

            CopySidePlacementToCustom(_customBaseSide);
            _localPosition = _customPosition;
            _localEulerAngles = _customEulerAngles;

            StoreActiveProfile();
            SavePreferences();
            SyncPlacementControls();
            ApplyPlacement();
        }

        private static void SetControlValue(IntElement? control, int value)
        {
            if (control != null)
                control.Value = value;
        }

        public static void SetEnabled(bool enabled)
        {
            if (_enabled == enabled)
            {
                if (enabled)
                    TryCreateCounter();

                return;
            }

            _enabled = enabled;
            if (!enabled)
            {
                DestroyCounter();
                MelonLogger.Msg("[Void Engine] Pouch ammo counter disabled.");
                return;
            }

            _nextRetry = 0f;
            LoggedSetupWarnings.Clear();
            TryCreateCounter();
            MelonLogger.Msg("[Void Engine] Pouch ammo counter enabled.");
        }

        public static void Update()
        {
            if (!_enabled)
                return;

            try
            {
                UpdateCounterVisibility();

                if (Time.time < _nextRefresh)
                    return;

                _nextRefresh = Time.time + RefreshInterval;
                Transform? currentReceiver = FindAmmoReceiver();
                UI_HUD? currentHud = FindSourceHud();
                if (currentReceiver == null || currentHud == null ||
                    _counterVisual == null || _ammoReceiver != currentReceiver || _sourceHud != currentHud)
                {
                    if (Time.time >= _nextRetry)
                    {
                        _nextRetry = Time.time + RetryInterval;
                        TryCreateCounter();
                    }
                }

                if (_counterVisual != null)
                    CopyAmmoText();
            }
            catch (System.Exception ex)
            {
                WarnSetupIssue($"Ammo counter update failed: {ex.Message}");
                DestroyCounter();
            }
        }

        private static void OnUIRigCreated()
        {
            if (!_enabled)
                return;

            DestroyCounter();
            _nextRetry = 0f;
            TryCreateCounter();
        }

        private static void OnLevelLoaded()
        {
            if (!_enabled)
                return;

            DestroyCounter();
            _nextRetry = 0f;
            TryCreateCounter();
        }

        private static Transform? FindAmmoReceiver()
        {
            Transform? rigTransform = Player.RigManager?.transform;
            if (rigTransform == null)
                return null;

            Transform? leftReceiver = rigTransform.Find(LeftAmmoReceiverPath);
            Transform? rightReceiver = rigTransform.Find(RightAmmoReceiverPath);
            PouchSide profileSide = _sidePreference;
            PouchSide receiverSide = profileSide == PouchSide.Custom ? _customBaseSide : profileSide;

            Transform? receiver = receiverSide == PouchSide.Left ? leftReceiver : rightReceiver;
            if (receiver == null)
            {
                Transform? fallback = receiverSide == PouchSide.Left ? rightReceiver : leftReceiver;
                if (fallback == null)
                    return null;

                receiver = fallback;
                PouchSide fallbackSide = receiverSide == PouchSide.Left ? PouchSide.Right : PouchSide.Left;
                WarnSetupIssue(
                    $"Requested {receiverSide} receiver was not found; using the {fallbackSide.ToString().ToLowerInvariant()} receiver with the {profileSide.ToString().ToLowerInvariant()} placement profile."
                );
            }

            if (_activeSide != profileSide)
                LoadProfile(profileSide);

            return receiver;
        }

        private static UI_HUD? FindSourceHud()
        {
            Transform? hudTransform = UIRig.Instance?.transform.Find("PLAYERUI/HUD");
            return hudTransform != null ? hudTransform.GetComponent<UI_HUD>() : null;
        }

        private static bool IsRadialMenuOpen()
        {
            PopUpMenuView? radialMenu = PopUpMenuView.instance;
            return radialMenu != null && radialMenu.isActive;
        }

        private static void UpdateCounterVisibility()
        {
            if (_counterVisual == null)
                return;

            bool isPlacementPageOpen = _placementPage != null &&
                ReferenceEquals(BoneLib.BoneMenu.Menu.CurrentPage, _placementPage);
            bool shouldBeVisible = !IsRadialMenuOpen() || isPlacementPageOpen;
            if (_counterVisual.activeSelf != shouldBeVisible)
                _counterVisual.SetActive(shouldBeVisible);
        }

        private static bool TryCreateCounter()
        {
            if (!_enabled)
                return false;

            GameObject? counter = null;
            try
            {
                Transform? receiver = FindAmmoReceiver();
                UI_HUD? sourceHud = FindSourceHud();
                GameObject? sourceVisual = sourceHud != null ? sourceHud.hud_AMMO : null;
                if (receiver == null || sourceHud == null || sourceVisual == null)
                {
                    WarnSetupIssue("Could not find the player pouch or BONELAB ammo panel yet.");
                    return false;
                }

                if (_counterVisual != null && _ammoReceiver == receiver && _sourceHud == sourceHud)
                    return true;

                DestroyCounter();

                MelonLogger.Msg("[Void Engine] Creating visual-only ammo panel copy.");
                counter = UnityEngine.Object.Instantiate(sourceVisual);
                counter.name = "VOIDWORKS_AMMO_PANEL";
                MelonLogger.Msg("[Void Engine] Ammo panel visuals cloned; binding displayed values.");
                if (counter.GetComponentInChildren<UI_HUD>(true) != null)
                {
                    UnityEngine.Object.Destroy(counter);
                    WarnSetupIssue("The ammo visual subtree unexpectedly contains a UI_HUD component.");
                    return false;
                }

                TMP_Text? sourceLight = sourceHud.text_ammo_light;
                TMP_Text? sourceMedium = sourceHud.text_ammo_medium;
                TMP_Text? sourceHeavy = sourceHud.text_ammo_heavy;
                TMP_Text? counterLight = FindMatchingText(counter.transform, sourceVisual.transform, sourceLight);
                TMP_Text? counterMedium = FindMatchingText(counter.transform, sourceVisual.transform, sourceMedium);
                TMP_Text? counterHeavy = FindMatchingText(counter.transform, sourceVisual.transform, sourceHeavy);
                if (sourceLight == null || sourceMedium == null || sourceHeavy == null ||
                    counterLight == null || counterMedium == null || counterHeavy == null)
                {
                    UnityEngine.Object.Destroy(counter);
                    WarnSetupIssue("Could not match BONELAB's three ammo labels to the pouch visual copy.");
                    return false;
                }

                counter.transform.SetParent(receiver, false);
                counter.transform.localPosition = _localPosition;
                counter.transform.localEulerAngles = _localEulerAngles;
                counter.transform.localScale = Vector3.one * 0.9f;
                bool isPlacementPageOpen = _placementPage != null &&
                    ReferenceEquals(BoneLib.BoneMenu.Menu.CurrentPage, _placementPage);
                counter.SetActive(!IsRadialMenuOpen() || isPlacementPageOpen);

                _counterVisual = counter;
                _ammoReceiver = receiver;
                _sourceHud = sourceHud;
                _sourceLight = sourceLight;
                _sourceMedium = sourceMedium;
                _sourceHeavy = sourceHeavy;
                _counterLight = counterLight;
                _counterMedium = counterMedium;
                _counterHeavy = counterHeavy;
                CopyAmmoText();

                MelonLogger.Msg("[Void Engine] Visual-only ammo panel attached to the pouch.");
                return true;
            }
            catch (System.Exception ex)
            {
                if (counter != null)
                    UnityEngine.Object.Destroy(counter);

                WarnSetupIssue($"Ammo panel setup failed: {ex.Message}");
                return false;
            }
        }

        private static TMP_Text? FindMatchingText(
            Transform counterRoot,
            Transform sourceRoot,
            TMP_Text? sourceText)
        {
            if (sourceText == null)
                return null;

            string? relativePath = GetRelativePath(sourceText.transform, sourceRoot);
            if (!string.IsNullOrEmpty(relativePath))
            {
                Transform? samePath = counterRoot.Find(relativePath);
                TMP_Text? samePathText = samePath != null ? samePath.GetComponent<TMP_Text>() : null;
                if (samePathText != null)
                    return samePathText;
            }

            foreach (TMP_Text candidate in counterRoot.GetComponentsInChildren<TMP_Text>(true))
            {
                if (candidate != null && candidate.gameObject.name == sourceText.gameObject.name)
                    return candidate;
            }

            return null;
        }

        private static string? GetRelativePath(Transform target, Transform root)
        {
            var segments = new System.Collections.Generic.List<string>();
            Transform? current = target;
            while (current != null && current != root)
            {
                segments.Add(current.name);
                current = current.parent;
            }

            if (current != root)
                return null;

            segments.Reverse();
            return string.Join("/", segments);
        }

        private static void CopyAmmoText()
        {
            CopyText(_sourceLight, _counterLight);
            CopyText(_sourceMedium, _counterMedium);
            CopyText(_sourceHeavy, _counterHeavy);
        }

        private static void CopyText(TMP_Text? source, TMP_Text? target)
        {
            if (source == null || target == null)
                return;

            target.text = source.text;
        }

        private static void WarnSetupIssue(string message)
        {
            if (!LoggedSetupWarnings.Add(message))
                return;

            MelonLogger.Warning($"[Void Engine] {message}");
        }

        private static void DestroyCounter()
        {
            if (_counterVisual != null)
                UnityEngine.Object.Destroy(_counterVisual);

            _counterVisual = null;
            _ammoReceiver = null;
            _sourceHud = null;
            _sourceLight = null;
            _sourceMedium = null;
            _sourceHeavy = null;
            _counterLight = null;
            _counterMedium = null;
            _counterHeavy = null;
        }
    }
}
