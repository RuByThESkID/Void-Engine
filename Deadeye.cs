using System;
using System.Collections.Generic;
using BoneLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.AI;
using MelonLoader;
using UnityEngine;

namespace VoidEngine
{
    public static class Deadeye
    {
        private const float NpcScanInterval = 0.75f;
        private static float _maximumTargetDistance = 50f;
        private const float HalfFieldOfView = 90f;
        private const float RetainTargetBias = 3f;
        private const int LineOfSightBufferSize = 64;

        private readonly struct PendingAim
        {
            public readonly Transform FirePoint;
            public readonly Quaternion OriginalLocalRotation;

            public PendingAim(Transform firePoint, Quaternion originalLocalRotation)
            {
                FirePoint = firePoint;
                OriginalLocalRotation = originalLocalRotation;
            }
        }

        private static readonly List<TriggerRefProxy> Npcs = new();
        private static readonly Dictionary<int, PendingAim> PendingAims = new();
        private static readonly Il2CppStructArray<RaycastHit> LineOfSightHits =
            new(LineOfSightBufferSize);
        private static bool _enabled;
        private static bool _voidworksEnabled;
        private static bool _manuallyDisabled;
        private static bool _slowMotionRequired = true;
        private static bool _hooksRegistered;
        private static bool _diagnosticsEnabled;
        private static bool _errorLogged;
        private static bool _hasSlowMoState;
        private static bool _lastSlowMoState;
        private static float _nextNpcScan;
        private static float _nextDiagnostic;
        private static string _scanStatus = "NPC scan has not run";
        private static string _leftStatus = "not checked";
        private static string _rightStatus = "not checked";
        private static TriggerRefProxy? _leftTarget;
        private static TriggerRefProxy? _rightTarget;

        public static void SetDiagnosticsEnabled(bool enabled)
        {
            _diagnosticsEnabled = enabled;
            _nextDiagnostic = 0f;
            MelonLogger.Msg($"[Void Engine] Deadeye diagnostics {(enabled ? "enabled" : "disabled")}.");
        }

        public static void SetVoidworksEnabled(bool enabled)
        {
            _voidworksEnabled = enabled;
            ApplyUserEnabledState();
        }

        public static void SetManuallyDisabled(bool disabled)
        {
            _manuallyDisabled = disabled;
            ApplyUserEnabledState();
        }

        public static void SetSlowMotionRequired(bool required)
        {
            if (_slowMotionRequired == required)
                return;

            _slowMotionRequired = required;
            _nextNpcScan = 0f;
            _leftTarget = null;
            _rightTarget = null;
            _leftStatus = "not checked";
            _rightStatus = "not checked";

            if (_diagnosticsEnabled)
                MelonLogger.Msg($"[Void Engine] Deadeye slo-mo requirement {(required ? "enabled" : "disabled")}.");
        }

        public static void SetMaximumTargetDistance(float distance)
        {
            if (float.IsNaN(distance) || distance <= 0f)
                return;

            _maximumTargetDistance = distance;
            _leftTarget = null;
            _rightTarget = null;
            string label = float.IsPositiveInfinity(distance) ? "Limitless" : $"{distance:0}m";
            MelonLogger.Msg($"[Void Engine] Deadeye effective distance set to {label}.");
        }

        public static void OnLevelLoaded()
        {
            RestorePendingAims();
            Npcs.Clear();
            _leftTarget = null;
            _rightTarget = null;
            _nextNpcScan = 0f;
            _scanStatus = "NPC scan has not run";
            _leftStatus = "not checked";
            _rightStatus = "not checked";
        }

        private static void ApplyUserEnabledState()
        {
            SetEnabled(_voidworksEnabled && !_manuallyDisabled);
        }

        private static void SetEnabled(bool enabled)
        {
            if (_enabled == enabled)
                return;

            _enabled = enabled;
            _errorLogged = false;
            _leftTarget = null;
            _rightTarget = null;
            Npcs.Clear();
            _nextNpcScan = 0f;
            _hasSlowMoState = false;
            _scanStatus = "NPC scan has not run";
            _leftStatus = "not checked";
            _rightStatus = "not checked";

            if (enabled)
                RegisterFireHooks();
            else
            {
                UnregisterFireHooks();
                RestorePendingAims();
            }

            MelonLogger.Msg(
                $"[Void Engine] Deadeye {(enabled ? "enabled" : "disabled")}; " +
                $"slo-mo required={_slowMotionRequired}."
            );
        }

        public static void Update()
        {
            bool slowMoActive = IsSlowMotionActive();
            if (_diagnosticsEnabled && (!_hasSlowMoState || slowMoActive != _lastSlowMoState))
            {
                _hasSlowMoState = true;
                _lastSlowMoState = slowMoActive;
                MelonLogger.Msg(
                    $"[Void Engine] Deadeye slow-motion state: {(slowMoActive ? "ACTIVE" : "inactive")}; " +
                    $"enabled={_enabled}, sloMoRequired={_slowMotionRequired}."
                );
            }

            if (_enabled && (!_slowMotionRequired || slowMoActive) && Time.unscaledTime >= _nextNpcScan)
                RefreshNpcCache();

            WriteDiagnosticsIfDue(slowMoActive);
        }

        private static void RegisterFireHooks()
        {
            if (_hooksRegistered)
                return;

            Hooking.OnPreFireGun += OnPreFireGun;
            Hooking.OnPostFireGun += OnPostFireGun;
            _hooksRegistered = true;
        }

        private static void UnregisterFireHooks()
        {
            if (!_hooksRegistered)
                return;

            Hooking.OnPreFireGun -= OnPreFireGun;
            Hooking.OnPostFireGun -= OnPostFireGun;
            _hooksRegistered = false;
        }

        private static void RefreshNpcCache()
        {
            _nextNpcScan = Time.unscaledTime + NpcScanInterval;
            try
            {
                Npcs.Clear();
                TriggerRefProxy[] proxies = UnityEngine.Object.FindObjectsOfType<TriggerRefProxy>(true);
                int validCount = 0;
                foreach (TriggerRefProxy proxy in proxies)
                {
                    if (IsValidTarget(proxy))
                    {
                        Npcs.Add(proxy);
                        validCount++;
                    }
                }

                _scanStatus = $"NPC scan found={proxies.Length}, alive targets={validCount}";
            }
            catch (Exception ex)
            {
                _scanStatus = $"NPC scan failed: {ex.GetType().Name}";
                LogErrorOnce($"NPC scan failed: {ex.Message}");
            }
        }

        private static void OnPreFireGun(Gun gun)
        {
            if (!_enabled || (_slowMotionRequired && !IsSlowMotionActive()) ||
                gun == null || gun.firePointTransform == null)
                return;

            try
            {
                Hand? leftHand = Player.LeftHand;
                Hand? rightHand = Player.RightHand;
                bool heldInLeft = IsHeldByHand(leftHand, gun);
                bool heldInRight = IsHeldByHand(rightHand, gun);
                if (!heldInLeft && !heldInRight)
                    return;

                if (Time.unscaledTime >= _nextNpcScan)
                    RefreshNpcCache();

                TriggerRefProxy? previousTarget = heldInLeft ? _leftTarget : _rightTarget;
                TriggerRefProxy? target = FindBestTarget(gun, previousTarget, out string targetStatus);
                if (heldInLeft)
                {
                    _leftTarget = target;
                    _leftStatus = target == null ? $"gun={gun.GetType().Name}, {targetStatus}" : $"gun={gun.GetType().Name}, shot corrected to '{target.gameObject.name}'";
                }
                if (heldInRight)
                {
                    _rightTarget = target;
                    _rightStatus = target == null ? $"gun={gun.GetType().Name}, {targetStatus}" : $"gun={gun.GetType().Name}, shot corrected to '{target.gameObject.name}'";
                }

                if (target == null || target.targetHead == null)
                    return;

                Transform firePoint = gun.firePointTransform;
                int gunId = gun.GetInstanceID();
                RestorePendingAim(gunId);
                PendingAims[gunId] = new PendingAim(firePoint, firePoint.localRotation);

                Vector3 direction = target.targetHead.position - firePoint.position;
                if (direction.sqrMagnitude < 0.0001f)
                {
                    PendingAims.Remove(gunId);
                    return;
                }

                firePoint.forward = direction.normalized;
            }
            catch (Exception ex)
            {
                LogErrorOnce($"pre-fire aim correction failed: {ex.Message}");
            }
        }

        private static void OnPostFireGun(Gun gun)
        {
            if (gun == null)
                return;

            try
            {
                RestorePendingAim(gun.GetInstanceID());
            }
            catch (Exception ex)
            {
                LogErrorOnce($"post-fire aim restore failed: {ex.Message}");
            }
        }

        private static bool IsHeldByHand(Hand? hand, Gun gun)
        {
            if (hand == null || hand.m_CurrentAttachedGO == null)
                return false;

            Gun? heldGun = FindHeldGun(hand);
            return heldGun != null && heldGun.GetInstanceID() == gun.GetInstanceID();
        }

        private static Gun? FindHeldGun(Hand hand)
        {
            GameObject attachedObject = hand.m_CurrentAttachedGO;
            Gun? gun = attachedObject.GetComponent<Gun>();
            if (gun == null)
                gun = attachedObject.GetComponentInChildren<Gun>(true);
            if (gun == null)
                gun = attachedObject.GetComponentInParent<Gun>();
            if (gun == null && hand.joint != null && hand.joint.connectedBody != null)
                gun = hand.joint.connectedBody.GetComponentInParent<Gun>();

            return gun;
        }

        private static TriggerRefProxy? FindBestTarget(
            Gun gun,
            TriggerRefProxy? previousTarget,
            out string targetStatus)
        {
            Transform muzzle = gun.firePointTransform;
            Vector3 forward = muzzle.forward;
            TriggerRefProxy? bestTarget = null;
            float bestScore = float.MaxValue;
            int validCount = 0;
            int tooFarOrNearCount = 0;
            int outsideAngleCount = 0;
            int blockedCount = 0;

            foreach (TriggerRefProxy proxy in Npcs)
            {
                if (!IsValidTarget(proxy) || proxy.targetHead == null)
                    continue;

                validCount++;
                Vector3 targetPosition = proxy.targetHead.position;
                Vector3 offset = targetPosition - muzzle.position;
                float distance = offset.magnitude;
                if (distance < 0.05f || distance > _maximumTargetDistance)
                {
                    tooFarOrNearCount++;
                    continue;
                }

                float angle = Vector3.Angle(forward, offset / distance);
                if (angle > HalfFieldOfView)
                {
                    outsideAngleCount++;
                    continue;
                }

                if (!HasLineOfSight(gun, proxy, targetPosition, distance))
                {
                    blockedCount++;
                    continue;
                }

                float score = angle;
                if (proxy == previousTarget)
                    score -= RetainTargetBias;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestTarget = proxy;
                }
            }

            targetStatus = bestTarget != null
                ? "target acquired"
                : $"no target (alive={validCount}, range={tooFarOrNearCount}, angle={outsideAngleCount}, blocked={blockedCount})";
            return bestTarget;
        }

        private static bool IsValidTarget(TriggerRefProxy? proxy)
        {
            return proxy != null &&
                   proxy.gameObject.activeInHierarchy &&
                   proxy.aiManager != null &&
                   !proxy.aiManager.isDead &&
                   proxy.targetHead != null;
        }

        private static bool HasLineOfSight(
            Gun gun,
            TriggerRefProxy target,
            Vector3 targetPosition,
            float distance)
        {
            Transform muzzle = gun.firePointTransform;
            Vector3 direction = (targetPosition - muzzle.position).normalized;
            Vector3 rayOrigin = muzzle.position + direction * 0.05f;
            float rayDistance = Mathf.Max(distance - 0.05f, 0f);
            Transform targetRoot = target.root != null ? target.root.transform : target.transform;
            Transform? localRig = Player.PhysicsRig != null ? Player.PhysicsRig.transform : null;
            int hitCount = Physics.RaycastNonAlloc(
                rayOrigin,
                direction,
                LineOfSightHits,
                rayDistance,
                ~0,
                QueryTriggerInteraction.Ignore
            );

            float nearestTargetDistance = float.MaxValue;
            float nearestBlockerDistance = float.MaxValue;
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = LineOfSightHits[i];
                Collider collider = hit.collider;
                if (collider == null || collider.isTrigger)
                    continue;

                Transform hitTransform = collider.transform;
                if (hitTransform == targetRoot || hitTransform.IsChildOf(targetRoot))
                {
                    nearestTargetDistance = Mathf.Min(nearestTargetDistance, hit.distance);
                    continue;
                }

                if (hitTransform == gun.transform || hitTransform.IsChildOf(gun.transform) ||
                    (localRig != null &&
                     (hitTransform == localRig || hitTransform.IsChildOf(localRig))))
                {
                    continue;
                }

                nearestBlockerDistance = Mathf.Min(nearestBlockerDistance, hit.distance);
            }

            if (nearestTargetDistance == float.MaxValue)
                return nearestBlockerDistance == float.MaxValue;

            return nearestTargetDistance <= nearestBlockerDistance;
        }

        private static void RestorePendingAim(int gunId)
        {
            if (!PendingAims.TryGetValue(gunId, out PendingAim pending))
                return;

            PendingAims.Remove(gunId);
            if (pending.FirePoint != null)
                pending.FirePoint.localRotation = pending.OriginalLocalRotation;
        }

        private static void RestorePendingAims()
        {
            foreach (PendingAim pending in PendingAims.Values)
            {
                if (pending.FirePoint != null)
                    pending.FirePoint.localRotation = pending.OriginalLocalRotation;
            }

            PendingAims.Clear();
        }

        private static bool IsSlowMotionActive()
        {
            return TimeManager.slowMoEnabled && Time.timeScale > 0.01f && Time.timeScale < 0.99f;
        }

        private static void WriteDiagnosticsIfDue(bool slowMoActive)
        {
            if (!_diagnosticsEnabled || Time.unscaledTime < _nextDiagnostic)
                return;

            _nextDiagnostic = Time.unscaledTime + 1f;
            MelonLogger.Msg(
                $"[Void Engine] Deadeye diagnostics | enabled={_enabled}, slowMo={slowMoActive}, " +
                $"sloMoRequired={_slowMotionRequired}, " +
                $"slowMoFlag={TimeManager.slowMoEnabled}, timeScale={Time.timeScale:0.00}, " +
                $"{_scanStatus}, LEFT: {_leftStatus}, RIGHT: {_rightStatus}"
            );
        }

        private static void LogErrorOnce(string message)
        {
            if (_errorLogged)
                return;

            _errorLogged = true;
            MelonLogger.Warning($"[Void Engine] Deadeye {message}");
        }
    }
}
