using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppSLZ.Marrow;
using MelonLoader;
using UnityEngine;

namespace VoidEngine
{
    internal static class PhysRigFusion
    {
        private const string SerializedControllerTypeName =
            "LabFusion.Marrow.Serialization.SerializedController";
        private const float RetryIntervalSeconds = 2f;

        private static readonly HarmonyLib.Harmony Harmony =
            new("VoidEngine.FusionFingerSync");
        private static readonly HashSet<int> LoggedControllerIds = new();
        private static bool _installed;
        private static bool _apiWarningLogged;
        private static float _nextInstallAttempt;
        private static FieldInfo? _indexCurlField;
        private static FieldInfo? _middleCurlField;
        private static FieldInfo? _ringCurlField;
        private static FieldInfo? _pinkyCurlField;

        public static void Initialize()
        {
            TryInstall();
        }

        public static void Update()
        {
            if (_installed || Time.unscaledTime < _nextInstallAttempt)
                return;

            TryInstall();
        }

        private static void TryInstall()
        {
            _nextInstallAttempt = Time.unscaledTime + RetryIntervalSeconds;

            try
            {
                Type? serializedControllerType = AccessTools.TypeByName(SerializedControllerTypeName);
                if (serializedControllerType == null)
                    return;

                ConstructorInfo? controllerConstructor = AccessTools.Constructor(
                    serializedControllerType,
                    new[] { typeof(BaseController) }
                );
                _indexCurlField = AccessTools.Field(serializedControllerType, "IndexCurl");
                _middleCurlField = AccessTools.Field(serializedControllerType, "MiddleCurl");
                _ringCurlField = AccessTools.Field(serializedControllerType, "RingCurl");
                _pinkyCurlField = AccessTools.Field(serializedControllerType, "PinkyCurl");
                MethodInfo? postfix = AccessTools.Method(
                    typeof(PhysRigFusion),
                    nameof(AfterControllerSerialized)
                );

                if (controllerConstructor == null ||
                    _indexCurlField == null || _middleCurlField == null ||
                    _ringCurlField == null || _pinkyCurlField == null ||
                    postfix == null)
                {
                    LogApiWarning("Fusion's controller-pose serialization API was not found.");
                    return;
                }

                Harmony.Patch(
                    controllerConstructor,
                    postfix: new HarmonyMethod(postfix)
                );

                _installed = true;
                MelonLogger.Msg(
                    "[Void Engine] Fusion finger-curl sync installed; active surface curls will use Fusion's player-pose stream."
                );
            }
            catch (Exception ex)
            {
                LogApiWarning($"Could not install Fusion finger-curl sync: {ex.Message}");
            }
        }

        private static void AfterControllerSerialized(object __instance, BaseController __0)
        {
            try
            {
                if (__instance == null || __0 == null ||
                    _indexCurlField == null || _middleCurlField == null ||
                    _ringCurlField == null || _pinkyCurlField == null ||
                    !PhysRig.TryGetNetworkFingerCurls(
                        __0,
                        out float index,
                        out float middle,
                        out float ring,
                        out float pinky))
                {
                    return;
                }

                _indexCurlField.SetValue(__instance, index);
                _middleCurlField.SetValue(__instance, middle);
                _ringCurlField.SetValue(__instance, ring);
                _pinkyCurlField.SetValue(__instance, pinky);

                if (LoggedControllerIds.Add(__0.GetInstanceID()))
                {
                    MelonLogger.Msg(
                        "[Void Engine] Fusion is sending Voidworks finger curls for this hand."
                    );
                }
            }
            catch (Exception ex)
            {
                LogApiWarning($"Fusion finger-curl update failed: {ex.Message}");
            }
        }

        private static void LogApiWarning(string message)
        {
            if (_apiWarningLogged)
                return;

            _apiWarningLogged = true;
            MelonLogger.Warning($"[Void Engine] {message}");
        }
    }
}