using System.Collections.Generic;
using BoneLib;
using Il2CppSLZ.Marrow;
using MelonLoader;
using UnityEngine;

namespace VoidEngine
{
    public static class PhysicsManager
    {
        public enum PhysicsQuality
        {
            NormalSet,
            HighSet,
            ExtremeSet,
            Adaptive
        }

        private const float RigidbodyScanInterval = 2f;
        private const float AdaptiveSolverUpdateInterval = 0.25f;
        private const float AdaptiveSolverBoostEnterSpeed = 1.5f;
        private const float AdaptiveSolverBoostExitSpeed = 0.75f;
        private const float AdaptiveSolverBoostEnterAngularSpeed = 3f;
        private const float AdaptiveSolverBoostExitAngularSpeed = 1.5f;
        private const float AdaptiveExternalCollisionUpdateInterval = 0.1f;
        private const float AdaptiveExternalCcdEnterSpeed = 3f;
        private const float AdaptiveExternalCcdExitSpeed = 2f;
        private const float AdaptiveExternalCcdEnterAngularSpeed = 8f;
        private const float AdaptiveExternalCcdExitAngularSpeed = 5f;
        private const float TargetMaxAngularVelocity = 40f;
        private const float BodyCollisionDynamicEnterSpeed = 3f;
        private const float BodyCollisionDynamicExitSpeed = 2f;
        private const float MinimumSlideDriveScale = 0.0001f;
        private const float GripEngagementThreshold = 0.30f;
        private const float SlideGripCurveExponent = 8f;

        private static bool _enabled;
        private static bool _collisionEnabled;
        private static bool _bodyCollisionEnabled;
        private static float _nextRigidbodyScan;
        private static float _nextAdaptiveSolverUpdate;
        private static float _nextAdaptiveExternalCollisionUpdate;
        private static PhysicsQuality _quality = PhysicsQuality.Adaptive;
        private static bool _knucklesAssistEnabled;
        private static bool _settingsCaptured;
        private static bool _updateErrorLogged;
        private static bool _collisionErrorLogged;
        private static bool _bodyCollisionErrorLogged;
        private static bool _adaptiveSolverErrorLogged;
        private static bool _detailedLoggingEnabled;
        private static bool _heldJointProbeLoggingEnabled;

        private static int _originalSolverIterations;
        private static int _originalSolverVelocityIterations;
        private static float _originalMaxAngularVelocity;
        private static int _lastAppliedDefaultSolverIterations;
        private static int _lastAppliedDefaultSolverVelocityIterations;
        private static float _lastAppliedDefaultMaxAngularVelocity;

        private readonly struct RigidbodyPhysicsSettings
        {
            public readonly int SolverIterations;
            public readonly int SolverVelocityIterations;
            public readonly float MaxAngularVelocity;

            public RigidbodyPhysicsSettings(Rigidbody body)
            {
                SolverIterations = body.solverIterations;
                SolverVelocityIterations = body.solverVelocityIterations;
                MaxAngularVelocity = body.maxAngularVelocity;
            }
        }

        private readonly struct JointDriveSettings
        {
            public readonly JointDrive XDrive;
            public readonly JointDrive YDrive;
            public readonly JointDrive ZDrive;
            public readonly JointDrive AngularXDrive;
            public readonly JointDrive AngularYZDrive;
            public readonly JointDrive SlerpDrive;

            public JointDriveSettings(ConfigurableJoint joint)
            {
                XDrive = joint.xDrive;
                YDrive = joint.yDrive;
                ZDrive = joint.zDrive;
                AngularXDrive = joint.angularXDrive;
                AngularYZDrive = joint.angularYZDrive;
                SlerpDrive = joint.slerpDrive;
            }
        }

        private static readonly Dictionary<Rigidbody, RigidbodyPhysicsSettings>
            _originalRigidbodySettings = new();

        private static readonly Dictionary<Rigidbody, (int Solver, int Velocity)>
            _lastAppliedSolverSettings = new();

        private static readonly Dictionary<Rigidbody, float>
            _lastAppliedMaxAngularVelocity = new();

        private static readonly Dictionary<Rigidbody, CollisionDetectionMode>
            _originalCollisionModes = new();

        private static readonly Dictionary<Rigidbody, CollisionDetectionMode>
            _originalBodyCollisionModes = new();

        private static readonly Dictionary<Rigidbody, CollisionDetectionMode>
            _lastAppliedCollisionModes = new();

        private static readonly HashSet<Rigidbody> _adaptiveSolverBoostedBodies = new();
        private static readonly HashSet<Rigidbody> _adaptiveImportantBodies = new();
        private static readonly HashSet<Rigidbody> _adaptiveExternalCollisionBodies = new();

        private static readonly Dictionary<ConfigurableJoint, JointDriveSettings>
            _originalJointDrives = new();

        private static readonly Dictionary<ConfigurableJoint, JointDriveSettings>
            _lastAppliedJointDrives = new();

        private static Rigidbody? _lastLeftHeldBody;
        private static Rigidbody? _lastRightHeldBody;
        private static ConfigurableJoint? _lastLeftHeldJoint;
        private static ConfigurableJoint? _lastRightHeldJoint;

        public static PhysicsQuality Quality => _quality;
        public static bool Enabled => _enabled;
        public static bool DetailedLoggingEnabled => _detailedLoggingEnabled;

        public static void SetDetailedLoggingEnabled(bool enabled)
        {
            _detailedLoggingEnabled = enabled;
        }

        public static void SetHeldJointProbeLoggingEnabled(bool enabled)
        {
            _heldJointProbeLoggingEnabled = enabled;
        }

        public static void OnLevelLoaded()
        {
            PruneDestroyedReferences();
            _nextRigidbodyScan = 0f;
            _nextAdaptiveSolverUpdate = 0f;
            _nextAdaptiveExternalCollisionUpdate = 0f;
            _adaptiveImportantBodies.Clear();
            if (_enabled)
                ApplyGlobalQualitySettings();
        }

        private static void LogVerbose(string message)
        {
            if (_detailedLoggingEnabled)
                MelonLogger.Msg(message);
        }

        public static void SetQuality(PhysicsQuality quality)
        {
            if (!System.Enum.IsDefined(typeof(PhysicsQuality), quality))
                return;

            if (_quality == quality)
                return;

            _quality = quality;
            _adaptiveSolverBoostedBodies.Clear();
            if (quality != PhysicsQuality.Adaptive)
                _adaptiveImportantBodies.Clear();
            if (_enabled)
            {
                ApplyGlobalQualitySettings();
                ApplySettingsToExistingRigidbodies(reapplyExisting: true);
                _nextAdaptiveSolverUpdate = Time.time + AdaptiveSolverUpdateInterval;
            }

            LogVerbose($"[Void Engine] Physics quality set to {_quality}.");
        }

        public static void SetCollisionEnabled(bool enabled)
        {
            if (_collisionEnabled == enabled)
                return;

            _collisionEnabled = enabled;
            if (enabled)
            {
                ApplyExternalCollisionModes();
                _nextRigidbodyScan = Time.time + RigidbodyScanInterval;
                _nextAdaptiveExternalCollisionUpdate = Time.time + AdaptiveExternalCollisionUpdateInterval;
            }
            else
            {
                RestoreCollisionModesForExternalObjects();
                _adaptiveExternalCollisionBodies.Clear();
            }

            LogVerbose(
                $"[Void Engine] External object collision {(enabled ? "enabled" : "disabled")}."
            );
        }

        public static void SetBodyCollisionEnabled(bool enabled)
        {
            if (_bodyCollisionEnabled == enabled)
                return;

            _bodyCollisionEnabled = enabled;
            _bodyCollisionErrorLogged = false;
            if (enabled)
            {
                ApplyBodyCollisionModes();
                _nextRigidbodyScan = Time.time + RigidbodyScanInterval;
            }
            else
            {
                RestoreBodyCollisionModes();
            }

            LogVerbose(
                $"[Void Engine] Player body collision {(enabled ? "enabled" : "disabled")}."
            );
        }

        public static void SetKnucklesAssistEnabled(bool enabled)
        {
            if (enabled == _knucklesAssistEnabled)
                return;

            _knucklesAssistEnabled = enabled;
            if (!enabled)
                RestoreAllJointDrives();

            LogVerbose(
                $"[Void Engine] Knuckles grip assist {(enabled ? "enabled" : "disabled")}."
            );
        }

        public static void SetEnabled(bool enabled)
        {
            if (enabled == _enabled)
                return;

            if (enabled)
            {
                if (Enable())
                {
                    _enabled = true;
                    _updateErrorLogged = false;
                }
            }
            else
            {
                _enabled = false;
                Disable();
            }
        }

        private static bool Enable()
        {
            try
            {
                _originalSolverIterations = Physics.defaultSolverIterations;
                _originalSolverVelocityIterations = Physics.defaultSolverVelocityIterations;
                _originalMaxAngularVelocity = Physics.defaultMaxAngularSpeed;
                _lastAppliedDefaultSolverIterations = _originalSolverIterations;
                _lastAppliedDefaultSolverVelocityIterations = _originalSolverVelocityIterations;
                _lastAppliedDefaultMaxAngularVelocity = _originalMaxAngularVelocity;
                _settingsCaptured = true;

                ApplyGlobalQualitySettings();

                ApplySettingsToExistingRigidbodies(reapplyExisting: true, logAffectedCount: true);
                _nextRigidbodyScan = Time.time + RigidbodyScanInterval;

                LogVerbose("[Void Engine] Voidworks ENABLED.");
                return true;
            }
            catch (System.Exception ex)
            {
                RestoreAllCollisionModes();
                RestoreAllRigidbodySettings();
                RestoreGlobalSettings();
                MelonLogger.Error($"[Void Engine] FAILED to enable physics: {ex}");
                return false;
            }
        }

        private static void Disable()
        {
            RestoreAllCollisionModes();
            RestoreAllJointDrives();
            RestoreAllRigidbodySettings();
            RestoreGlobalSettings();

            _lastLeftHeldBody = null;
            _lastRightHeldBody = null;
            _lastLeftHeldJoint = null;
            _lastRightHeldJoint = null;
            if (_collisionEnabled)
                ApplyExternalCollisionModes();
            LogVerbose("[Void Engine] Voidworks DISABLED.");
        }

        private static void ApplyGlobalQualitySettings()
        {
            GetSolverIterations(_quality, out int solverIterations, out int velocityIterations);
            if (_quality == PhysicsQuality.Adaptive)
            {
                Physics.defaultSolverIterations = _originalSolverIterations;
                Physics.defaultSolverVelocityIterations = _originalSolverVelocityIterations;
            }
            else
            {
                Physics.defaultSolverIterations = solverIterations;
                Physics.defaultSolverVelocityIterations = velocityIterations;
            }

            Physics.defaultMaxAngularSpeed = TargetMaxAngularVelocity;
            _lastAppliedDefaultSolverIterations = Physics.defaultSolverIterations;
            _lastAppliedDefaultSolverVelocityIterations = Physics.defaultSolverVelocityIterations;
            _lastAppliedDefaultMaxAngularVelocity = Physics.defaultMaxAngularSpeed;
        }

        private static void GetSolverIterations(
            PhysicsQuality quality,
            out int solverIterations,
            out int velocityIterations)
        {
            switch (quality)
            {
                case PhysicsQuality.Adaptive:
                    solverIterations = 16;
                    velocityIterations = 6;
                    break;
                case PhysicsQuality.NormalSet:
                    solverIterations = 12;
                    velocityIterations = 4;
                    break;
                case PhysicsQuality.HighSet:
                    solverIterations = 16;
                    velocityIterations = 6;
                    break;
                case PhysicsQuality.ExtremeSet:
                    solverIterations = 24;
                    velocityIterations = 10;
                    break;
                default:
                    solverIterations = 16;
                    velocityIterations = 6;
                    break;
            }
        }

        private static void ApplySettingsToExistingRigidbodies(
            bool reapplyExisting = false,
            bool logAffectedCount = false)
        {
            int appliedCount = 0;
            var bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>(true);
            GetSolverIterations(_quality, out int solverIterations, out int velocityIterations);
            if (_quality == PhysicsQuality.Adaptive)
                RefreshAdaptiveImportantBodies();

            foreach (Rigidbody body in bodies)
            {
                if (body == null)
                    continue;

                try
                {
                    if (!_originalRigidbodySettings.ContainsKey(body))
                        _originalRigidbodySettings.Add(body, new RigidbodyPhysicsSettings(body));
                    else if (!reapplyExisting)
                        continue;

                    RigidbodyPhysicsSettings original = _originalRigidbodySettings[body];
                    if (_quality == PhysicsQuality.Adaptive)
                    {
                        ApplyAdaptiveSolverSettings(
                            body,
                            original,
                            solverIterations,
                            velocityIterations
                        );
                    }
                    else
                    {
                        _adaptiveSolverBoostedBodies.Remove(body);
                        body.solverIterations = solverIterations;
                        body.solverVelocityIterations = velocityIterations;
                    }
                    _lastAppliedSolverSettings[body] =
                        (body.solverIterations, body.solverVelocityIterations);

                    if (body.maxAngularVelocity < TargetMaxAngularVelocity)
                    {
                        body.maxAngularVelocity = TargetMaxAngularVelocity;
                        _lastAppliedMaxAngularVelocity[body] = body.maxAngularVelocity;
                    }
                    appliedCount++;
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Could not apply Voidworks settings to a rigidbody: {ex.Message}"
                    );
                }
            }

            if (logAffectedCount)
            {
                MelonLogger.Msg(
                    $"[Void Engine] Initial physics setup affected {appliedCount} rigidbodies " +
                    $"with {_quality} solver settings."
                );
            }
            else if (appliedCount > 0)
                LogVerbose($"[Void Engine] Applied {_quality} solver settings to {appliedCount} rigidbodies.");
        }

        private static int ApplyAdaptiveSolverSettings(
            Rigidbody body,
            RigidbodyPhysicsSettings original,
            int boostedIterations,
            int boostedVelocityIterations)
        {
            bool wasBoosted = _adaptiveSolverBoostedBodies.Contains(body);
            float linearThreshold = wasBoosted
                ? AdaptiveSolverBoostExitSpeed
                : AdaptiveSolverBoostEnterSpeed;
            float angularThreshold = wasBoosted
                ? AdaptiveSolverBoostExitAngularSpeed
                : AdaptiveSolverBoostEnterAngularSpeed;
            bool isImportantBody = _adaptiveImportantBodies.Contains(body) ||
                body == _lastLeftHeldBody || body == _lastRightHeldBody;
            bool isMovingQuickly =
                body.velocity.sqrMagnitude >= linearThreshold * linearThreshold ||
                body.angularVelocity.sqrMagnitude >= angularThreshold * angularThreshold;
            bool shouldBoost = !body.isKinematic && (isImportantBody || isMovingQuickly);

            if (shouldBoost)
            {
                body.solverIterations = boostedIterations;
                body.solverVelocityIterations = boostedVelocityIterations;
                _adaptiveSolverBoostedBodies.Add(body);
            }
            else
            {
                body.solverIterations = original.SolverIterations;
                body.solverVelocityIterations = original.SolverVelocityIterations;
                _adaptiveSolverBoostedBodies.Remove(body);
            }

            _lastAppliedSolverSettings[body] =
                (body.solverIterations, body.solverVelocityIterations);

            return wasBoosted == shouldBoost ? 0 : shouldBoost ? 1 : -1;
        }

        private static void UpdateAdaptiveSolverSettings()
        {
            if (!_enabled || _quality != PhysicsQuality.Adaptive ||
                Time.time < _nextAdaptiveSolverUpdate)
            {
                return;
            }

            _nextAdaptiveSolverUpdate = Time.time + AdaptiveSolverUpdateInterval;
            GetSolverIterations(_quality, out int solverIterations, out int velocityIterations);
            int boostedCount = 0;
            int restoredCount = 0;

            foreach (KeyValuePair<Rigidbody, RigidbodyPhysicsSettings> entry in _originalRigidbodySettings)
            {
                Rigidbody body = entry.Key;
                if (body == null)
                    continue;

                try
                {
                    int change = ApplyAdaptiveSolverSettings(
                        body,
                        entry.Value,
                        solverIterations,
                        velocityIterations
                    );
                    if (change > 0)
                        boostedCount++;
                    else if (change < 0)
                        restoredCount++;
                }
                catch (System.Exception ex)
                {
                    if (!_adaptiveSolverErrorLogged)
                    {
                        MelonLogger.Warning(
                            $"[Void Engine] Could not update adaptive solver quality: {ex.Message}"
                        );
                        _adaptiveSolverErrorLogged = true;
                    }
                }
            }

            if (boostedCount > 0 || restoredCount > 0)
            {
                LogVerbose(
                    $"[Void Engine] Adaptive solver changed bodies: " +
                    $"boosted={boostedCount}, restored-to-original={restoredCount}."
                );
            }
        }

        private static void RestoreAllRigidbodySettings()
        {
            _adaptiveSolverBoostedBodies.Clear();
            _adaptiveImportantBodies.Clear();
            if (_originalRigidbodySettings.Count == 0)
                return;

            var bodies = new List<Rigidbody>(_originalRigidbodySettings.Keys);
            foreach (Rigidbody body in bodies)
            {
                try
                {
                    if (body != null && _originalRigidbodySettings.TryGetValue(body, out RigidbodyPhysicsSettings original))
                    {
                        if (_lastAppliedSolverSettings.TryGetValue(body, out var lastSolver))
                        {
                            if (body.solverIterations == lastSolver.Solver)
                                body.solverIterations = original.SolverIterations;
                            if (body.solverVelocityIterations == lastSolver.Velocity)
                                body.solverVelocityIterations = original.SolverVelocityIterations;
                        }

                        if (_lastAppliedMaxAngularVelocity.TryGetValue(body, out float lastAngularVelocity) &&
                            Mathf.Approximately(body.maxAngularVelocity, lastAngularVelocity))
                        {
                            body.maxAngularVelocity = original.MaxAngularVelocity;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Could not restore a rigidbody's physics settings: {ex.Message}"
                    );
                }
                finally
                {
                    _originalRigidbodySettings.Remove(body);
                    _lastAppliedSolverSettings.Remove(body);
                    _lastAppliedMaxAngularVelocity.Remove(body);
                }
            }
        }

        private static void RestoreGlobalSettings()
        {
            if (!_settingsCaptured)
                return;

            try
            {
                if (Physics.defaultSolverIterations == _lastAppliedDefaultSolverIterations)
                    Physics.defaultSolverIterations = _originalSolverIterations;
                if (Physics.defaultSolverVelocityIterations == _lastAppliedDefaultSolverVelocityIterations)
                    Physics.defaultSolverVelocityIterations = _originalSolverVelocityIterations;
                if (Mathf.Approximately(
                        Physics.defaultMaxAngularSpeed,
                        _lastAppliedDefaultMaxAngularVelocity))
                {
                    Physics.defaultMaxAngularSpeed = _originalMaxAngularVelocity;
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Error($"[Void Engine] FAILED to restore global physics settings: {ex}");
            }
            finally
            {
                _settingsCaptured = false;
            }
        }

        public static void UpdateHeldObjectCollision()
        {
            if ((_enabled || _collisionEnabled || _bodyCollisionEnabled) && Time.time >= _nextRigidbodyScan)
            {
                _nextRigidbodyScan = Time.time + RigidbodyScanInterval;
                PruneDestroyedReferences();

                if (_enabled)
                    ApplySettingsToExistingRigidbodies();

                if (_collisionEnabled)
                    ApplyExternalCollisionModes();

                if (_bodyCollisionEnabled)
                    ApplyBodyCollisionModes();
            }

            if (_collisionEnabled && Time.time >= _nextAdaptiveExternalCollisionUpdate)
            {
                _nextAdaptiveExternalCollisionUpdate = Time.time + AdaptiveExternalCollisionUpdateInterval;
                UpdateAdaptiveExternalCollisionModes();
            }

            if (_bodyCollisionEnabled)
                UpdateAdaptiveBodyCollisionModes();

            if (!_enabled)
                return;

            UpdateHand(true);
            UpdateHand(false);
            UpdateAdaptiveSolverSettings();
        }

        private static void PruneDestroyedReferences()
        {
            var staleBodies = new List<Rigidbody>();
            foreach (Rigidbody body in _originalRigidbodySettings.Keys)
            {
                if (body == null)
                    staleBodies.Add(body!);
            }
            foreach (Rigidbody body in staleBodies)
            {
                _originalRigidbodySettings.Remove(body);
                _lastAppliedSolverSettings.Remove(body);
                _lastAppliedMaxAngularVelocity.Remove(body);
                _adaptiveSolverBoostedBodies.Remove(body);
                _adaptiveImportantBodies.Remove(body);
                _adaptiveExternalCollisionBodies.Remove(body);
            }

            staleBodies.Clear();
            foreach (Rigidbody body in _originalCollisionModes.Keys)
            {
                if (body == null)
                    staleBodies.Add(body!);
            }
            foreach (Rigidbody body in staleBodies)
            {
                _originalCollisionModes.Remove(body);
                _lastAppliedCollisionModes.Remove(body);
            }

            staleBodies.Clear();
            foreach (Rigidbody body in _originalBodyCollisionModes.Keys)
            {
                if (body == null)
                    staleBodies.Add(body!);
            }
            foreach (Rigidbody body in staleBodies)
            {
                _originalBodyCollisionModes.Remove(body);
                _lastAppliedCollisionModes.Remove(body);
            }

            var staleJoints = new List<ConfigurableJoint>();
            foreach (ConfigurableJoint joint in _originalJointDrives.Keys)
            {
                if (joint == null)
                    staleJoints.Add(joint!);
            }
            foreach (ConfigurableJoint joint in staleJoints)
            {
                _originalJointDrives.Remove(joint);
                _lastAppliedJointDrives.Remove(joint);
            }

            if (_lastLeftHeldBody == null)
                _lastLeftHeldBody = null;
            if (_lastRightHeldBody == null)
                _lastRightHeldBody = null;
            if (_lastLeftHeldJoint == null)
                _lastLeftHeldJoint = null;
            if (_lastRightHeldJoint == null)
                _lastRightHeldJoint = null;
        }

        private static void ApplyExternalCollisionModes()
        {
            int changedCount = 0;
            var bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();

            foreach (Rigidbody body in bodies)
            {
                if (body == null || body.isKinematic)
                    continue;

                try
                {
                    if (body.GetComponentInParent<RigManager>() != null)
                        continue;

                    if (IsHeldByEitherHand(body))
                    {
                        _adaptiveExternalCollisionBodies.Remove(body);
                        continue;
                    }

                    CollisionDetectionMode currentMode = body.collisionDetectionMode;
                    if (currentMode != CollisionDetectionMode.Discrete &&
                        !_originalCollisionModes.ContainsKey(body))
                    {
                        continue;
                    }

                    if (!_originalCollisionModes.ContainsKey(body))
                        _originalCollisionModes.Add(body, currentMode);

                    CollisionDetectionMode originalMode = _originalCollisionModes[body];
                    if (originalMode == CollisionDetectionMode.Discrete)
                    {
                        _adaptiveExternalCollisionBodies.Add(body);
                        if (ApplyAdaptiveExternalCollisionMode(body, originalMode))
                            changedCount++;
                    }
                }
                catch (System.Exception ex)
                {
                    if (!_collisionErrorLogged)
                    {
                        MelonLogger.Warning(
                            $"[Void Engine] Could not update an external rigidbody collision mode: {ex.Message}"
                        );
                        _collisionErrorLogged = true;
                    }
                }
            }

            if (changedCount > 0)
            {
                LogVerbose(
                    $"[Void Engine] Adaptive external collision updated on {changedCount} rigidbodies."
                );
            }
        }

        private static void UpdateAdaptiveExternalCollisionModes()
        {
            int enabledCount = 0;
            int restoredCount = 0;

            try
            {
                foreach (Rigidbody body in _adaptiveExternalCollisionBodies)
                {
                    if (body == null || body.isKinematic ||
                        body == _lastLeftHeldBody || body == _lastRightHeldBody)
                    {
                        continue;
                    }

                    if (!_originalCollisionModes.TryGetValue(body, out CollisionDetectionMode originalMode))
                        continue;

                    CollisionDetectionMode previousMode = body.collisionDetectionMode;
                    if (!ApplyAdaptiveExternalCollisionMode(body, originalMode))
                        continue;

                    if (body.collisionDetectionMode == CollisionDetectionMode.ContinuousSpeculative &&
                        previousMode != CollisionDetectionMode.ContinuousSpeculative)
                    {
                        enabledCount++;
                    }
                    else if (previousMode == CollisionDetectionMode.ContinuousSpeculative)
                    {
                        restoredCount++;
                    }
                }

                if (enabledCount > 0 || restoredCount > 0)
                {
                    LogVerbose(
                        $"[Void Engine] Adaptive external collision changed modes: " +
                        $"speculative={enabledCount}, returned-to-original={restoredCount}."
                    );
                }
            }
            catch (System.Exception ex)
            {
                if (!_collisionErrorLogged)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Could not update adaptive external collision: {ex.Message}"
                    );
                    _collisionErrorLogged = true;
                }
            }
        }

        private static bool ApplyAdaptiveExternalCollisionMode(
            Rigidbody body,
            CollisionDetectionMode originalMode)
        {
            if (originalMode != CollisionDetectionMode.Discrete)
                return false;

            CollisionDetectionMode currentMode = body.collisionDetectionMode;
            bool wasSpeculative = currentMode == CollisionDetectionMode.ContinuousSpeculative;
            float linearThreshold = wasSpeculative
                ? AdaptiveExternalCcdExitSpeed
                : AdaptiveExternalCcdEnterSpeed;
            float angularThreshold = wasSpeculative
                ? AdaptiveExternalCcdExitAngularSpeed
                : AdaptiveExternalCcdEnterAngularSpeed;
            bool movingFast =
                body.velocity.sqrMagnitude >= linearThreshold * linearThreshold ||
                body.angularVelocity.sqrMagnitude >= angularThreshold * angularThreshold;
            CollisionDetectionMode targetMode = movingFast
                ? CollisionDetectionMode.ContinuousSpeculative
                : CollisionDetectionMode.Discrete;

            if (currentMode == targetMode)
                return false;

            SetCollisionDetectionMode(body, targetMode);
            return true;
        }

        private static void ApplyBodyCollisionModes()
        {
            try
            {
                PhysicsRig physicsRig = Player.PhysicsRig;
                if (physicsRig == null)
                {
                    RestoreBodyCollisionModes();
                    return;
                }

                var bodies = physicsRig.GetComponentsInChildren<Rigidbody>(false);
                var currentBodies = new HashSet<Rigidbody>();
                int changedCount = 0;

                foreach (Rigidbody body in bodies)
                {
                    if (body == null)
                        continue;

                    currentBodies.Add(body);
                    if (body.isKinematic)
                    {
                        RestoreBodyCollisionMode(body);
                        continue;
                    }

                    if (!_originalBodyCollisionModes.ContainsKey(body))
                        _originalBodyCollisionModes.Add(body, body.collisionDetectionMode);

                    CollisionDetectionMode originalMode = _originalBodyCollisionModes[body];
                    if (ApplyAdaptiveBodyCollisionMode(body, originalMode))
                        changedCount++;
                }

                RestoreBodyCollisionModesOutsideRig(currentBodies);

                if (changedCount > 0)
                {
                    LogVerbose(
                        $"[Void Engine] Adaptive collision enabled on {changedCount} player-rig rigidbodies."
                    );
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Could not update player-rig collision modes: {ex.Message}"
                );
            }
        }

        private static void RefreshAdaptiveImportantBodies()
        {
            try
            {
                _adaptiveImportantBodies.Clear();

                PhysicsRig physicsRig = Player.PhysicsRig;
                if (physicsRig != null)
                {
                    foreach (Rigidbody body in physicsRig.GetComponentsInChildren<Rigidbody>(false))
                    {
                        if (body != null)
                            _adaptiveImportantBodies.Add(body);
                    }
                }

                Joint[] joints = UnityEngine.Object.FindObjectsOfType<Joint>(true);
                foreach (Joint joint in joints)
                {
                    if (joint == null)
                        continue;

                    Rigidbody jointBody = joint.GetComponent<Rigidbody>();
                    if (jointBody != null)
                        _adaptiveImportantBodies.Add(jointBody);

                    Rigidbody connectedBody = joint.connectedBody;
                    if (connectedBody != null)
                        _adaptiveImportantBodies.Add(connectedBody);
                }
            }
            catch (System.Exception ex)
            {
                if (!_adaptiveSolverErrorLogged)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Could not refresh adaptive solver targets: {ex.Message}"
                    );
                    _adaptiveSolverErrorLogged = true;
                }
            }
        }

        private static void UpdateAdaptiveBodyCollisionModes()
        {
            int escalatedCount = 0;
            int relaxedCount = 0;

            try
            {
                foreach (KeyValuePair<Rigidbody, CollisionDetectionMode> entry in _originalBodyCollisionModes)
                {
                    Rigidbody body = entry.Key;
                    if (body == null || body.isKinematic)
                        continue;

                    CollisionDetectionMode previousMode = body.collisionDetectionMode;
                    if (!ApplyAdaptiveBodyCollisionMode(body, entry.Value))
                        continue;

                    if (body.collisionDetectionMode == CollisionDetectionMode.ContinuousDynamic &&
                        previousMode != CollisionDetectionMode.ContinuousDynamic)
                    {
                        escalatedCount++;
                    }
                    else if (previousMode == CollisionDetectionMode.ContinuousDynamic)
                    {
                        relaxedCount++;
                    }
                }

                if (escalatedCount > 0 || relaxedCount > 0)
                {
                    LogVerbose(
                        $"[Void Engine] Adaptive body collision changed modes: " +
                        $"dynamic={escalatedCount}, returned-to-baseline={relaxedCount}."
                    );
                }
            }
            catch (System.Exception ex)
            {
                if (!_bodyCollisionErrorLogged)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Could not update adaptive player body collision: {ex.Message}"
                    );
                    _bodyCollisionErrorLogged = true;
                }
            }
        }

        private static bool ApplyAdaptiveBodyCollisionMode(
            Rigidbody body,
            CollisionDetectionMode originalMode)
        {
            CollisionDetectionMode currentMode = body.collisionDetectionMode;
            CollisionDetectionMode baselineMode = originalMode switch
            {
                CollisionDetectionMode.ContinuousDynamic => CollisionDetectionMode.ContinuousDynamic,
                CollisionDetectionMode.Continuous => CollisionDetectionMode.Continuous,
                _ => CollisionDetectionMode.ContinuousSpeculative
            };

            float speedSquared = body.velocity.sqrMagnitude;
            bool wasEscalated = currentMode == CollisionDetectionMode.ContinuousDynamic &&
                baselineMode != CollisionDetectionMode.ContinuousDynamic;
            float threshold = wasEscalated
                ? BodyCollisionDynamicExitSpeed
                : BodyCollisionDynamicEnterSpeed;
            bool shouldUseDynamic = baselineMode == CollisionDetectionMode.ContinuousDynamic ||
                speedSquared >= threshold * threshold;
            CollisionDetectionMode targetMode = shouldUseDynamic
                ? CollisionDetectionMode.ContinuousDynamic
                : baselineMode;

            if (currentMode == targetMode)
                return false;

            SetCollisionDetectionMode(body, targetMode);
            return true;
        }

        private static void RestoreBodyCollisionModes()
        {
            if (_originalBodyCollisionModes.Count == 0)
                return;

            var bodies = new List<Rigidbody>(_originalBodyCollisionModes.Keys);
            foreach (Rigidbody body in bodies)
            {
                try
                {
                    if (body != null &&
                        _originalBodyCollisionModes.TryGetValue(body, out CollisionDetectionMode originalMode))
                    {
                        RestoreCollisionDetectionModeIfUnchanged(body, originalMode);
                    }
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Could not restore a player-rig collision mode: {ex.Message}"
                    );
                }
                finally
                {
                    _originalBodyCollisionModes.Remove(body);
                }
            }
        }

        private static void RestoreBodyCollisionModesOutsideRig(HashSet<Rigidbody> currentBodies)
        {
            var trackedBodies = new List<Rigidbody>(_originalBodyCollisionModes.Keys);
            foreach (Rigidbody body in trackedBodies)
            {
                if (body == null || !currentBodies.Contains(body))
                    RestoreBodyCollisionMode(body!);
            }
        }

        private static void RestoreBodyCollisionMode(Rigidbody body)
        {
            if (!_originalBodyCollisionModes.TryGetValue(body, out CollisionDetectionMode originalMode))
                return;

            try
            {
                RestoreCollisionDetectionModeIfUnchanged(body, originalMode);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Could not restore a replaced player-rig collision mode: {ex.Message}"
                );
            }
            finally
            {
                _originalBodyCollisionModes.Remove(body);
            }
        }

        private static bool IsTrackedHeldBody(Rigidbody body)
        {
            return (_lastLeftHeldBody != null && _lastLeftHeldBody == body) ||
                   (_lastRightHeldBody != null && _lastRightHeldBody == body);
        }

        private static bool IsHeldByEitherHand(Rigidbody body)
        {
            if (IsTrackedHeldBody(body))
                return true;

            try
            {
                Hand leftHand = Player.LeftHand;
                if (leftHand != null && leftHand.joint != null && leftHand.joint.connectedBody == body)
                    return true;

                Hand rightHand = Player.RightHand;
                return rightHand != null && rightHand.joint != null && rightHand.joint.connectedBody == body;
            }
            catch
            {
                return false;
            }
        }

        private static void RestoreCollisionModesForExternalObjects()
        {
            if (_originalCollisionModes.Count == 0)
            {
                _adaptiveExternalCollisionBodies.Clear();
                return;
            }

            var bodies = new List<Rigidbody>(_originalCollisionModes.Keys);
            foreach (Rigidbody body in bodies)
            {
                if (_enabled && body != null && IsTrackedHeldBody(body))
                    continue;

                RestoreCollisionMode(body!);
            }
        }

        private static void UpdateHand(bool left)
        {
            try
            {
                if (left)
                {
                    ApplyHeldCollision(
                        Player.LeftHand,
                        ref _lastLeftHeldBody,
                        ref _lastLeftHeldJoint,
                        "LEFT"
                    );
                }
                else
                {
                    ApplyHeldCollision(
                        Player.RightHand,
                        ref _lastRightHeldBody,
                        ref _lastRightHeldJoint,
                        "RIGHT"
                    );
                }
            }
            catch (System.Exception ex)
            {
                if (!_updateErrorLogged)
                {
                    MelonLogger.Error($"[Void Engine] FAILED while checking held objects: {ex}");
                    _updateErrorLogged = true;
                }
            }
        }

        private static void ApplyHeldCollision(
            Hand hand,
            ref Rigidbody? lastHeldBody,
            ref ConfigurableJoint? lastHeldJoint,
            string handName)
        {
            if (hand == null)
            {
                ReleaseHeldState(ref lastHeldBody, ref lastHeldJoint, handName);
                return;
            }

            ConfigurableJoint joint = hand.joint;
            if (joint == null)
            {
                ReleaseHeldState(ref lastHeldBody, ref lastHeldJoint, handName);
                return;
            }

            Rigidbody heldBody = joint.connectedBody;
            if (heldBody == null || heldBody.isKinematic)
            {
                ReleaseHeldState(ref lastHeldBody, ref lastHeldJoint, handName);
                return;
            }

            if (heldBody != lastHeldBody || joint != lastHeldJoint)
            {
                if (lastHeldBody != null || lastHeldJoint != null)
                    ReleaseHeldState(ref lastHeldBody, ref lastHeldJoint, handName);

                lastHeldBody = heldBody;
                lastHeldJoint = joint;
                _adaptiveExternalCollisionBodies.Remove(heldBody);
                _nextAdaptiveSolverUpdate = Time.time;

                if (!_originalCollisionModes.ContainsKey(heldBody))
                    _originalCollisionModes[heldBody] = heldBody.collisionDetectionMode;

                LogHeldJointSettings(hand.joint, heldBody, handName);
            }

            ApplyGripScaledDrives(joint, handName == "LEFT");

            if (heldBody.collisionDetectionMode != CollisionDetectionMode.ContinuousDynamic)
                SetCollisionDetectionMode(heldBody, CollisionDetectionMode.ContinuousDynamic);
        }

        private static void ApplyGripScaledDrives(ConfigurableJoint joint, bool leftHand)
        {
            if (!_knucklesAssistEnabled)
                return;

            if (!_originalJointDrives.TryGetValue(joint, out JointDriveSettings originalDrives))
            {
                originalDrives = new JointDriveSettings(joint);
                _originalJointDrives.Add(joint, originalDrives);
            }

            float grip = KnucklesIntegration.TryGetGripStrength(leftHand, out float value)
                ? Mathf.Clamp01(value)
                : 1f;
            float heldGripRange = Mathf.InverseLerp(GripEngagementThreshold, 1f, grip);
            float slideScale = Mathf.Lerp(
                MinimumSlideDriveScale,
                1f,
                Mathf.Pow(heldGripRange, SlideGripCurveExponent)
            );

            joint.xDrive = ScaleDrive(originalDrives.XDrive, slideScale);
            joint.yDrive = originalDrives.YDrive;
            joint.zDrive = originalDrives.ZDrive;
            joint.angularXDrive = originalDrives.AngularXDrive;
            joint.angularYZDrive = originalDrives.AngularYZDrive;
            joint.slerpDrive = originalDrives.SlerpDrive;
            _lastAppliedJointDrives[joint] = new JointDriveSettings(joint);
        }

        private static JointDrive ScaleDrive(JointDrive drive, float scale)
        {
            drive.positionSpring *= scale;
            drive.positionDamper *= scale;
            drive.maximumForce *= scale;
            return drive;
        }

        private static void LogHeldJointSettings(ConfigurableJoint joint, Rigidbody heldBody, string handName)
        {
            if (!_heldJointProbeLoggingEnabled)
                return;

            bool hasGrip = KnucklesIntegration.TryGetGripStrength(handName == "LEFT", out float grip);
            string gripText = hasGrip ? grip.ToString("0.00") : "unavailable";

            MelonLogger.Msg(
                $"[Void Engine] Held joint probe | {handName} | " +
                $"object={heldBody.name}, grip={gripText}, " +
                $"axis={joint.axis}, secondaryAxis={joint.secondaryAxis}, " +
                $"motion=({joint.xMotion},{joint.yMotion},{joint.zMotion}), " +
                $"angular=({joint.angularXMotion},{joint.angularYMotion},{joint.angularZMotion}), " +
                $"breakForce={joint.breakForce:0.##}, breakTorque={joint.breakTorque:0.##}, " +
                $"massScale={joint.massScale:0.##}, connectedMassScale={joint.connectedMassScale:0.##}, " +
                $"xDrive=({FormatDrive(joint.xDrive)}), " +
                $"yDrive=({FormatDrive(joint.yDrive)}), " +
                $"zDrive=({FormatDrive(joint.zDrive)}), " +
                $"angularXDrive=({FormatDrive(joint.angularXDrive)}), " +
                $"angularYZDrive=({FormatDrive(joint.angularYZDrive)}), " +
                $"slerpDrive=({FormatDrive(joint.slerpDrive)})"
            );
        }

        private static string FormatDrive(JointDrive drive)
        {
            return $"spring={drive.positionSpring:0.##},damper={drive.positionDamper:0.##},max={drive.maximumForce:0.##}";
        }

        private static void ReleaseHeldState(
            ref Rigidbody? heldBody,
            ref ConfigurableJoint? heldJoint,
            string handName)
        {
            if (heldJoint != null)
            {
                RestoreJointDrives(heldJoint);
                heldJoint = null;
            }

            if (heldBody == null)
            {
                heldBody = null;
                return;
            }

            Rigidbody body = heldBody;
            _nextAdaptiveSolverUpdate = Time.time;
            if (!IsHeldByOtherHand(body, handName))
            {
                RestoreCollisionMode(body);
                if (_collisionEnabled)
                    _nextRigidbodyScan = Time.time;
            }

            heldBody = null;
        }

        private static bool IsHeldByOtherHand(Rigidbody body, string releasingHand)
        {
            if (releasingHand != "LEFT" && _lastLeftHeldBody == body)
                return true;

            if (releasingHand != "RIGHT" && _lastRightHeldBody == body)
                return true;

            return false;
        }

        private static void SetCollisionDetectionMode(
            Rigidbody body,
            CollisionDetectionMode mode)
        {
            body.collisionDetectionMode = mode;
            _lastAppliedCollisionModes[body] = mode;
        }

        private static void RestoreCollisionDetectionModeIfUnchanged(
            Rigidbody body,
            CollisionDetectionMode originalMode)
        {
            if (body != null &&
                _lastAppliedCollisionModes.TryGetValue(body, out CollisionDetectionMode lastAppliedMode) &&
                body.collisionDetectionMode == lastAppliedMode)
            {
                body.collisionDetectionMode = originalMode;
            }

            _lastAppliedCollisionModes.Remove(body!);
        }

        private static void RestoreCollisionMode(Rigidbody body)
        {
            _adaptiveExternalCollisionBodies.Remove(body);
            if (!_originalCollisionModes.TryGetValue(body, out CollisionDetectionMode originalMode))
                return;

            try
            {
                RestoreCollisionDetectionModeIfUnchanged(body, originalMode);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[Void Engine] Could not restore a held object's collision mode: {ex.Message}");
            }
            finally
            {
                _originalCollisionModes.Remove(body);
            }
        }

        private static void RestoreAllCollisionModes()
        {
            _adaptiveExternalCollisionBodies.Clear();
            if (_originalCollisionModes.Count == 0)
                return;

            var bodies = new List<Rigidbody>(_originalCollisionModes.Keys);
            foreach (Rigidbody body in bodies)
                RestoreCollisionMode(body);

            _originalCollisionModes.Clear();
        }

        private static void RestoreJointDrives(ConfigurableJoint joint)
        {
            if (!_originalJointDrives.TryGetValue(joint, out JointDriveSettings original))
                return;

            try
            {
                if (joint != null)
                {
                    if (_lastAppliedJointDrives.TryGetValue(joint, out JointDriveSettings last))
                    {
                        if (DriveMatches(joint.xDrive, last.XDrive))
                            joint.xDrive = original.XDrive;
                        if (DriveMatches(joint.yDrive, last.YDrive))
                            joint.yDrive = original.YDrive;
                        if (DriveMatches(joint.zDrive, last.ZDrive))
                            joint.zDrive = original.ZDrive;
                        if (DriveMatches(joint.angularXDrive, last.AngularXDrive))
                            joint.angularXDrive = original.AngularXDrive;
                        if (DriveMatches(joint.angularYZDrive, last.AngularYZDrive))
                            joint.angularYZDrive = original.AngularYZDrive;
                        if (DriveMatches(joint.slerpDrive, last.SlerpDrive))
                            joint.slerpDrive = original.SlerpDrive;
                    }
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[Void Engine] Could not restore a held joint's drives: {ex.Message}");
            }
            finally
            {
                _originalJointDrives.Remove(joint);
                _lastAppliedJointDrives.Remove(joint);
            }
        }

        private static bool DriveMatches(JointDrive current, JointDrive expected)
        {
            return Mathf.Approximately(current.positionSpring, expected.positionSpring) &&
                   Mathf.Approximately(current.positionDamper, expected.positionDamper) &&
                   Mathf.Approximately(current.maximumForce, expected.maximumForce);
        }

        private static void RestoreAllJointDrives()
        {
            if (_originalJointDrives.Count == 0)
                return;

            var joints = new List<ConfigurableJoint>(_originalJointDrives.Keys);
            foreach (ConfigurableJoint joint in joints)
                RestoreJointDrives(joint);

            _originalJointDrives.Clear();
        }
    }
}
