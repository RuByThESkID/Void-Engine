using MelonLoader;
using UnityEngine;
using UnityEngine.XR;

namespace VoidEngine
{
    public static class KnucklesIntegration
    {
        private const float ProbeLogInterval = 0.25f;

        private static bool _probeEnabled;
        private static float _nextProbeLogTime;
        private static float _leftGripStrength;
        private static float _rightGripStrength;
        private static bool _leftGripAvailable;
        private static bool _rightGripAvailable;

        public static void SetProbeEnabled(bool enabled)
        {
            _probeEnabled = enabled;
            _nextProbeLogTime = 0f;

            MelonLogger.Msg(
                enabled
                    ? "[Void Engine] Grip input probe started (4 samples per second)."
                    : "[Void Engine] Grip input probe stopped."
            );
        }

        public static void Update()
        {
            string left = ReadGrip(XRNode.LeftHand, "LEFT", out _leftGripAvailable, out _leftGripStrength);
            string right = ReadGrip(XRNode.RightHand, "RIGHT", out _rightGripAvailable, out _rightGripStrength);

            if (!_probeEnabled || Time.unscaledTime < _nextProbeLogTime)
                return;

            _nextProbeLogTime = Time.unscaledTime + ProbeLogInterval;

            MelonLogger.Msg(
                $"[Void Engine] Grip probe | {left} | {right}"
            );
        }

        public static bool TryGetGripStrength(bool leftHand, out float gripStrength)
        {
            bool available = leftHand ? _leftGripAvailable : _rightGripAvailable;
            gripStrength = leftHand ? _leftGripStrength : _rightGripStrength;
            return available;
        }

        private static string ReadGrip(
            XRNode node,
            string handName,
            out bool gripAvailable,
            out float cachedGripStrength)
        {
            gripAvailable = false;
            cachedGripStrength = 0f;

            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid)
                return $"{handName}: no XR device";

            bool hasGripValue = device.TryGetFeatureValue(CommonUsages.grip, out float gripValue);
            bool hasGripButton = device.TryGetFeatureValue(CommonUsages.gripButton, out bool gripButton);

            if (hasGripValue)
            {
                cachedGripStrength = Mathf.Clamp01(gripValue);
                gripAvailable = true;
            }

            string analog = hasGripValue
                ? cachedGripStrength.ToString("0.00")
                : "unsupported";
            string button = hasGripButton
                ? (gripButton ? "pressed" : "released")
                : "unsupported";
            string deviceName = string.IsNullOrEmpty(device.name) ? "unnamed device" : device.name;

            return $"{handName} ({deviceName}): grip={analog}, gripButton={button}";
        }
    }
}
