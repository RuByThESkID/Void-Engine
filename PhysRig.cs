using System.Collections.Generic;
using BoneLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSLZ.Marrow;
using MelonLoader;
using UnityEngine;
using UnityEngine.XR;
using MarrowHand = Il2CppSLZ.Marrow.Hand;

namespace VoidEngine
{
    public static class PhysRig
    {
        private const float ProbeLogInterval = 0.25f;
        private const float DeepCrouchFadeStart = 0.60f;
        private const float DownReachStartMeters = 0.20f;
        private const float DownReachFullMeters = 0.75f;
        private const float OutwardReachStartMeters = 0.20f;
        private const float OutwardReachFullMeters = 0.65f;
        private const float MaximumCandidateReduction = 0.70f;
        private const float WeightSmoothingSeconds = 0.20f;
        private const float FingerProbeDistanceScale = 1.8f;
        private const float MaximumSurfaceCurl = 0.85f;
        private const float FingerTargetSmoothingSeconds = 0.045f;
        private const float FingerCurlEngageSpeed = 16f;
        private const float FingerCurlReleaseSpeed = 4f;
        private const float HeldObjectCurlEngageSpeed = 16f;
        private const float HeldObjectCurlReleaseSpeed = 2.5f;
        private const float GrabHoverRadiusScale = 0.60f;
        private const float GrabHoverScanInterval = 0.5f;
        private const float GrabHoverFingerGraceSeconds = 0.18f;
        private const float FingerOverrideReleaseThreshold = 0.025f;
        private const int FingerRaycastBufferSize = 32;

        private sealed class FingerCurlState
        {
            public bool Initialized;
            public float Index;
            public float Middle;
            public float Ring;
            public float Pinky;
            public float FilteredIndexTarget;
            public float FilteredMiddleTarget;
            public float FilteredRingTarget;
            public float FilteredPinkyTarget;
        }

        private sealed class HandHoverSettings
        {
            public MarrowHand Hand = null!;
            public float OriginalRadius;
        }

        private static bool _diagnosticsEnabled;
        private static bool _fingerDiagnosticsEnabled;
        private static bool _fingerFixEnabled;
        private static bool _grabHoverReductionEnabled;
        private static bool _hasStandingBaseline;
        private static bool _trackingWarningLogged;
        private static bool _fingerErrorLogged;
        private static float _standingHeadHeight;
        private static float _nextLogTime;
        private static float _leftWeight = 1f;
        private static float _rightWeight = 1f;
        private static float _nextGrabHoverScan;
        private static readonly HashSet<int> FingerOverrideActive = new();
        private static readonly HashSet<int> FingerContactLogged = new();
        private static readonly Dictionary<int, FingerCurlState> FingerCurlStates = new();
        private static readonly Dictionary<int, HandHoverSettings> HandHoverSettingsById = new();
        private static readonly Dictionary<int, float> GrabHoverFingerSuspendUntil = new();
        private static readonly List<Transform> HeldObjectRoots = new(4);
        private static readonly Il2CppStructArray<RaycastHit> FingerRaycastHits =
            new(FingerRaycastBufferSize);

        public static void SetFingerFixEnabled(bool enabled)
        {
            if (_fingerFixEnabled == enabled)
                return;

            _fingerFixEnabled = enabled;
            _fingerErrorLogged = false;

            if (!enabled)
            {
                ClearFingerOverride(Player.LeftHand);
                ClearFingerOverride(Player.RightHand);
                FingerOverrideActive.Clear();
                FingerContactLogged.Clear();
                FingerCurlStates.Clear();
                GrabHoverFingerSuspendUntil.Clear();
            }

            if (PhysicsManager.DetailedLoggingEnabled)
                MelonLogger.Msg($"[Void Engine] Finger anti-clip {(enabled ? "enabled" : "disabled")}.");
        }

        public static void SetGrabHoverReductionEnabled(bool enabled)
        {
            if (_grabHoverReductionEnabled == enabled)
                return;

            _grabHoverReductionEnabled = enabled;
            _nextGrabHoverScan = 0f;

            if (!enabled)
                RestoreGrabHoverSettings();

            if (PhysicsManager.DetailedLoggingEnabled)
                MelonLogger.Msg($"[Void Engine] Reduced grab hover {(enabled ? "enabled" : "disabled")}.");
        }

        public static void SetFingerDiagnosticsEnabled(bool enabled)
        {
            _fingerDiagnosticsEnabled = enabled;
            FingerContactLogged.Clear();
        }

        internal static bool TryGetNetworkFingerCurls(
            BaseController controller,
            out float index,
            out float middle,
            out float ring,
            out float pinky)
        {
            index = middle = ring = pinky = 0f;
            if (controller == null)
                return false;

            if (TryGetNetworkFingerCurls(Player.LeftHand, controller, out index, out middle, out ring, out pinky))
                return true;

            return TryGetNetworkFingerCurls(Player.RightHand, controller, out index, out middle, out ring, out pinky);
        }

        private static bool TryGetNetworkFingerCurls(
            MarrowHand hand,
            BaseController controller,
            out float index,
            out float middle,
            out float ring,
            out float pinky)
        {
            index = middle = ring = pinky = 0f;
            if (hand == null || hand.Controller == null ||
                hand.Controller.GetInstanceID() != controller.GetInstanceID())
            {
                return false;
            }

            int handId = hand.GetInstanceID();
            if (!FingerOverrideActive.Contains(handId) ||
                !FingerCurlStates.TryGetValue(handId, out FingerCurlState? state))
            {
                return false;
            }

            index = state.Index;
            middle = state.Middle;
            ring = state.Ring;
            pinky = state.Pinky;
            return true;
        }

        public static void SetDiagnosticsEnabled(bool enabled)
        {
            _diagnosticsEnabled = enabled;
            _nextLogTime = 0f;
            _trackingWarningLogged = false;

            if (enabled)
            {
                _hasStandingBaseline = false;
                _leftWeight = 1f;
                _rightWeight = 1f;
                MelonLogger.Msg(
                    "[Void Engine] Body-rig diagnostics started. Stand upright while the standing-height baseline is captured."
                );
            }
            else
            {
                MelonLogger.Msg("[Void Engine] Body-rig diagnostics stopped.");
            }
        }

        public static void Update()
        {
            UpdateGrabHoverReduction();
            UpdateFingerFix();

            if (!_diagnosticsEnabled)
                return;

            bool shouldLog = Time.unscaledTime >= _nextLogTime;
            if (shouldLog)
                _nextLogTime = Time.unscaledTime + ProbeLogInterval;

            if (!TryReadPosition(XRNode.Head, out Vector3 headPosition) ||
                !TryReadPosition(XRNode.LeftHand, out Vector3 leftPosition) ||
                !TryReadPosition(XRNode.RightHand, out Vector3 rightPosition))
            {
                if (shouldLog && !_trackingWarningLogged)
                {
                    MelonLogger.Warning(
                        "[Void Engine] Body-rig probe could not read tracked head and both hand positions."
                    );
                    _trackingWarningLogged = true;
                }
                return;
            }

            _trackingWarningLogged = false;

            if (!_hasStandingBaseline)
            {
                _standingHeadHeight = headPosition.y;
                _hasStandingBaseline = true;
                MelonLogger.Msg(
                    $"[Void Engine] Standing head-height baseline captured: {_standingHeadHeight:0.00} m."
                );
            }

            float crouchDepth = Mathf.Clamp01(
                (_standingHeadHeight - headPosition.y) / 0.75f
            );
            float deepCrouchFade = Mathf.InverseLerp(
                DeepCrouchFadeStart,
                1f,
                crouchDepth
            );

            float leftTargetWeight = CalculateCandidateWeight(
                headPosition,
                leftPosition,
                deepCrouchFade
            );
            float rightTargetWeight = CalculateCandidateWeight(
                headPosition,
                rightPosition,
                deepCrouchFade
            );

            float smoothing = 1f - Mathf.Exp(
                -Time.unscaledDeltaTime / WeightSmoothingSeconds
            );
            _leftWeight = Mathf.Lerp(_leftWeight, leftTargetWeight, smoothing);
            _rightWeight = Mathf.Lerp(_rightWeight, rightTargetWeight, smoothing);

            if (shouldLog)
            {
                MelonLogger.Msg(
                    $"[Void Engine] Body-rig probe | headY={headPosition.y:0.00}m, " +
                    $"standY={_standingHeadHeight:0.00}m, crouch={crouchDepth:0.00}, " +
                    $"deepCrouchFade={deepCrouchFade:0.00}, " +
                    $"leftCandidate={_leftWeight:0.00} (target={leftTargetWeight:0.00}), " +
                    $"rightCandidate={_rightWeight:0.00} (target={rightTargetWeight:0.00})"
                );
            }
        }

        private static void UpdateGrabHoverReduction()
        {
            if (!_grabHoverReductionEnabled || Time.unscaledTime < _nextGrabHoverScan)
                return;

            _nextGrabHoverScan = Time.unscaledTime + GrabHoverScanInterval;

            try
            {
                ApplyReducedGrabHover(Player.LeftHand);
                ApplyReducedGrabHover(Player.RightHand);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Grab hover adjustment failed: {ex.Message}"
                );
            }
        }

        private static void ApplyReducedGrabHover(MarrowHand hand)
        {
            if (hand == null)
                return;

            int handId = hand.GetInstanceID();
            if (!HandHoverSettingsById.TryGetValue(handId, out HandHoverSettings? settings) ||
                settings.Hand == null)
            {
                settings = new HandHoverSettings
                {
                    Hand = hand,
                    OriginalRadius = hand.HoverSphereRadius
                };
                HandHoverSettingsById[handId] = settings;
            }

            hand.HoverSphereRadius = settings.OriginalRadius * GrabHoverRadiusScale;
        }

        private static void RestoreGrabHoverSettings()
        {
            foreach (HandHoverSettings settings in HandHoverSettingsById.Values)
            {
                try
                {
                    if (settings.Hand == null)
                        continue;

                    settings.Hand.HoverSphereRadius = settings.OriginalRadius;
                }
                catch
                {
                }
            }

            HandHoverSettingsById.Clear();
        }

        private static void UpdateFingerFix()
        {
            if (!_fingerFixEnabled)
                return;

            try
            {
                HeldObjectRoots.Clear();
                AddHeldObjectRoot(Player.LeftHand);
                AddHeldObjectRoot(Player.RightHand);

                Transform? playerRig = null;
                RigManager rigManager = Player.RigManager;
                if (rigManager != null)
                    playerRig = rigManager.transform;

                bool leftSucceeded = TryApplyFingerFix(Player.LeftHand, playerRig);
                bool rightSucceeded = TryApplyFingerFix(Player.RightHand, playerRig);
                if (leftSucceeded && rightSucceeded)
                    _fingerErrorLogged = false;
            }
            catch (System.Exception ex)
            {
                if (!_fingerErrorLogged)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Finger anti-clip update failed: {ex.Message}"
                    );
                    _fingerErrorLogged = true;
                }
            }
        }

        private static void AddHeldObjectRoot(MarrowHand hand)
        {
            if (hand == null)
                return;

            GameObject attachedObject = hand.m_CurrentAttachedGO;
            if (attachedObject != null)
                AddInteractableRoot(attachedObject.transform);

            ConfigurableJoint? joint = hand.joint;
            Rigidbody? connectedBody = joint != null ? joint.connectedBody : null;
            if (connectedBody != null)
                AddInteractableRoot(connectedBody.transform);
        }

        private static void AddInteractableRoot(Transform objectTransform)
        {
            InteractableHost host = objectTransform.GetComponentInParent<InteractableHost>();
            Transform root = host != null ? host.transform : objectTransform;
            if (!HeldObjectRoots.Contains(root))
                HeldObjectRoots.Add(root);
        }

        private static bool TryApplyFingerFix(MarrowHand hand, Transform? playerRig)
        {
            try
            {
                ApplyFingerFix(hand, playerRig);
                return true;
            }
            catch (System.Exception ex)
            {
                if (!_fingerErrorLogged)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Finger anti-clip update failed: {ex.Message}"
                    );
                    _fingerErrorLogged = true;
                }

                return false;
            }
        }

        private static void ApplyFingerFix(MarrowHand hand, Transform? playerRig)
        {
            if (hand == null)
                return;

            HandPoseAnimator animator = hand.Animator;
            if (animator == null)
                return;

            int handId = hand.GetInstanceID();
            BaseController controller = hand.Controller;
            bool grabTargetPresent =
                hand.HoveringReceiver != null ||
                hand.farHoveringReciever != null;
            bool grabInputActive = controller != null && controller.isGrabInputPressedFinal;
            if (grabTargetPresent || grabInputActive)
            {
                GrabHoverFingerSuspendUntil[handId] =
                    Time.unscaledTime + GrabHoverFingerGraceSeconds;
            }

            bool grabInteractionActive = grabTargetPresent || grabInputActive ||
                (GrabHoverFingerSuspendUntil.TryGetValue(handId, out float suspendUntil) &&
                 Time.unscaledTime < suspendUntil);
            bool hasConnectedJoint = hand.joint != null && hand.joint.connectedBody != null;
            if (hand.HasAttachedObject() ||
                hand.m_CurrentAttachedGO != null ||
                hasConnectedJoint ||
                grabInteractionActive)
            {
                if (_fingerDiagnosticsEnabled && grabInteractionActive && FingerOverrideActive.Contains(handId))
                {
                    MelonLogger.Msg(
                        $"[Void Engine] Finger anti-clip yielded to BONELAB grab hover | hand={hand.name}."
                    );
                }

                ClearFingerOverride(hand, animator, handId);
                return;
            }

            float indexInput = controller != null ? Mathf.Clamp01(controller._processedIndex) : 0f;
            float middleInput = controller != null ? Mathf.Clamp01(controller._processedMiddle) : 0f;
            float ringInput = controller != null ? Mathf.Clamp01(controller._processedRing) : 0f;
            float pinkyInput = controller != null ? Mathf.Clamp01(controller._processedPinky) : 0f;

            float indexCurl = GetSurfaceCurl(
                animator.index1, animator.index2, animator.index3, indexInput, playerRig
            );
            float middleCurl = GetSurfaceCurl(
                animator.middle1, animator.middle2, animator.middle3, middleInput, playerRig
            );
            float ringCurl = GetSurfaceCurl(
                animator.ring1, animator.ring2, animator.ring3, ringInput, playerRig
            );
            float pinkyCurl = GetSurfaceCurl(
                animator.pinky1, animator.pinky2, animator.pinky3, pinkyInput, playerRig
            );

            if (!FingerCurlStates.TryGetValue(handId, out FingerCurlState? state))
            {
                state = new FingerCurlState();
                FingerCurlStates.Add(handId, state);
            }

            bool holdingObject = HeldObjectRoots.Count > 0;
            float engageSpeed = holdingObject
                ? HeldObjectCurlEngageSpeed
                : FingerCurlEngageSpeed;
            float releaseSpeed = holdingObject
                ? HeldObjectCurlReleaseSpeed
                : FingerCurlReleaseSpeed;
            float deltaTime = Mathf.Max(Time.unscaledDeltaTime, 0f);
            state.FilteredIndexTarget = FilterFingerTarget(
                state.FilteredIndexTarget, indexCurl, indexInput, deltaTime, state.Initialized
            );
            state.FilteredMiddleTarget = FilterFingerTarget(
                state.FilteredMiddleTarget, middleCurl, middleInput, deltaTime, state.Initialized
            );
            state.FilteredRingTarget = FilterFingerTarget(
                state.FilteredRingTarget, ringCurl, ringInput, deltaTime, state.Initialized
            );
            state.FilteredPinkyTarget = FilterFingerTarget(
                state.FilteredPinkyTarget, pinkyCurl, pinkyInput, deltaTime, state.Initialized
            );

            float indexOverride = BlendFingerCurl(
                state.Index, state.FilteredIndexTarget, indexCurl >= 0f,
                indexInput, deltaTime, engageSpeed, releaseSpeed, state.Initialized
            );
            float middleOverride = BlendFingerCurl(
                state.Middle, state.FilteredMiddleTarget, middleCurl >= 0f,
                middleInput, deltaTime, engageSpeed, releaseSpeed, state.Initialized
            );
            float ringOverride = BlendFingerCurl(
                state.Ring, state.FilteredRingTarget, ringCurl >= 0f,
                ringInput, deltaTime, engageSpeed, releaseSpeed, state.Initialized
            );
            float pinkyOverride = BlendFingerCurl(
                state.Pinky, state.FilteredPinkyTarget, pinkyCurl >= 0f,
                pinkyInput, deltaTime, engageSpeed, releaseSpeed, state.Initialized
            );
            state.Index = indexOverride >= 0f ? indexOverride : indexInput;
            state.Middle = middleOverride >= 0f ? middleOverride : middleInput;
            state.Ring = ringOverride >= 0f ? ringOverride : ringInput;
            state.Pinky = pinkyOverride >= 0f ? pinkyOverride : pinkyInput;
            state.Initialized = true;

            if (indexOverride >= 0f || middleOverride >= 0f || ringOverride >= 0f || pinkyOverride >= 0f)
            {
                animator.CurlOverride(-1f, indexOverride, middleOverride, ringOverride, pinkyOverride);
                FingerOverrideActive.Add(handId);
                if (_fingerDiagnosticsEnabled && FingerContactLogged.Add(handId))
                {
                    MelonLogger.Msg(
                        $"[Void Engine] Finger surface contact | hand={hand.name}, " +
                        $"index={indexOverride:0.00}, middle={middleOverride:0.00}, " +
                        $"ring={ringOverride:0.00}, pinky={pinkyOverride:0.00}."
                    );
                }
            }
            else
            {
                ClearFingerOverride(hand, animator, handId);
            }
        }

        private static float BlendFingerCurl(
            float previousCurl,
            float filteredTarget,
            bool surfaceContact,
            float inputCurl,
            float deltaTime,
            float engageSpeed,
            float releaseSpeed,
            bool initialized)
        {
            float targetCurl = Mathf.Max(inputCurl, filteredTarget);
            float currentCurl = initialized ? previousCurl : inputCurl;
            float speed = targetCurl > currentCurl ? engageSpeed : releaseSpeed;
            float blendedCurl = Mathf.MoveTowards(currentCurl, targetCurl, speed * deltaTime);

            if (!surfaceContact && Mathf.Abs(blendedCurl - inputCurl) <= FingerOverrideReleaseThreshold)
                return -1f;

            return blendedCurl;
        }

        private static float FilterFingerTarget(
            float previousTarget,
            float surfaceCurl,
            float inputCurl,
            float deltaTime,
            bool initialized)
        {
            float target = surfaceCurl >= 0f
                ? Mathf.Max(inputCurl, surfaceCurl)
                : inputCurl;
            if (!initialized)
                return target;

            float smoothing = 1f - Mathf.Exp(-deltaTime / FingerTargetSmoothingSeconds);
            return Mathf.Lerp(previousTarget, target, smoothing);
        }

        private static float GetSurfaceCurl(
            Transform proximal,
            Transform middle,
            Transform tip,
            float inputCurl,
            Transform? playerRig)
        {
            if (proximal == null || middle == null || tip == null)
                return -1f;

            Vector3 start = proximal.position;
            Vector3 direction = middle.position - start;
            float firstSegmentLength = direction.magnitude;
            if (firstSegmentLength < 0.00001f)
                return -1f;

            direction /= firstSegmentLength;
            float distalSegmentLength = Vector3.Distance(tip.position, middle.position);
            float fingerLength = Vector3.Distance(start, tip.position);
            float rayLength = firstSegmentLength + distalSegmentLength * FingerProbeDistanceScale;
            Vector3 rayOrigin = start - direction * 0.005f;

            int hitCount = Physics.RaycastNonAlloc(
                rayOrigin,
                direction,
                FingerRaycastHits,
                rayLength,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore
            );

            float nearestDistance = float.MaxValue;
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = FingerRaycastHits[i];
                Collider collider = hit.collider;
                if (collider == null || collider.isTrigger ||
                    (playerRig != null && collider.transform.IsChildOf(playerRig)) ||
                    collider.GetComponentInParent<RigManager>() != null)
                {
                    continue;
                }

                if (hit.distance < nearestDistance)
                    nearestDistance = hit.distance;
            }

            if (nearestDistance >= rayLength)
                return -1f;

            float clearance = nearestDistance - fingerLength;
            float probeMargin = Mathf.Max(rayLength - fingerLength, 0.0001f);
            float proximity = Mathf.InverseLerp(probeMargin, 0f, clearance);
            float surfaceCurl = Mathf.Clamp01(proximity) * MaximumSurfaceCurl;
            return surfaceCurl > inputCurl ? surfaceCurl : -1f;
        }

        private static void ClearFingerOverride(MarrowHand hand)
        {
            if (hand == null)
                return;

            HandPoseAnimator animator = hand.Animator;
            if (animator != null)
                ClearFingerOverride(hand, animator, hand.GetInstanceID());
        }

        private static void ClearFingerOverride(MarrowHand hand, HandPoseAnimator animator, int handId)
        {
            if (FingerOverrideActive.Remove(handId))
                animator.CurlOverride(-1f, -1f, -1f, -1f, -1f);

            FingerCurlStates.Remove(handId);
        }

        private static float CalculateCandidateWeight(
            Vector3 headPosition,
            Vector3 handPosition,
            float deepCrouchFade)
        {
            Vector3 headToHand = handPosition - headPosition;
            float handBelowHead = -headToHand.y;
            float horizontalReach = Mathf.Sqrt(
                headToHand.x * headToHand.x + headToHand.z * headToHand.z
            );

            float downwardReach = Mathf.InverseLerp(
                DownReachStartMeters,
                DownReachFullMeters,
                handBelowHead
            );
            float outwardReach = Mathf.InverseLerp(
                OutwardReachStartMeters,
                OutwardReachFullMeters,
                horizontalReach
            );
            float reachIntent = downwardReach * outwardReach;

            return 1f - MaximumCandidateReduction * deepCrouchFade * reachIntent;
        }

        private static bool TryReadPosition(XRNode node, out Vector3 position)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid ||
                !device.TryGetFeatureValue(CommonUsages.devicePosition, out position))
            {
                position = Vector3.zero;
                return false;
            }

            return true;
        }
    }
}
