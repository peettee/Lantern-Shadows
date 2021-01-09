using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace pp.RaftMods.LanternShadows
{
    /// <summary>
    /// Enables realtime shadows on lantern buildables and other block lights.
    /// </summary>
    public class CLanternShadows : Mod
    {
        public const string VERSION = "1.0.0";
        public const string APP_IDENT = "pp.RaftMods.LanternShadows";

        public static bool PreventDayNightLightSwitch { get; set; } = false;

        private static LightShadows LightShadowType => mi_settings.Current.graphics.shadowType == ShadowQuality.All ? LightShadows.Soft :
                                                        mi_settings.Current.graphics.shadowType == ShadowQuality.HardOnly ? LightShadows.Hard :
                                                            LightShadows.None;

        private static List<Light> mi_sceneLights;
        private static Settings mi_settings;

        private Harmony mi_harmony;

        private void Start()
        {
            mi_harmony = new Harmony(APP_IDENT);
            mi_harmony.PatchAll(Assembly.GetExecutingAssembly());

            mi_settings = ComponentManager<Settings>.Value;

            LoadLights();

            SceneManager.activeSceneChanged -= OnSceneLoaded;
            SceneManager.activeSceneChanged += OnSceneLoaded;

            Debug.Log("LanternShadows v. " + VERSION + " loaded.");
        }

        private void OnDestroy()
        {
            if (mi_sceneLights != null)
            {
                foreach (var light in mi_sceneLights)
                {
                    if (light) light.shadows = LightShadows.None;
                }
                mi_sceneLights = null;
            }

            if (mi_harmony != null)
            {
                mi_harmony.UnpatchAll(APP_IDENT);
                mi_harmony = null;
            }

            mi_settings = null;

            SceneManager.activeSceneChanged -= OnSceneLoaded;
        }

        private static void LoadLights()
        {
            if (RAPI.IsCurrentSceneGame())
            {
                mi_sceneLights = GameObject.FindObjectsOfType<LightSingularity>()
                                    .Select(_o => _o.LightComponent)
                                    .ToList();

                var shadowType = LightShadowType;
                foreach (var light in mi_sceneLights)
                {
                    light.shadows = shadowType;
                }
            }
        }

        private static void RegisterLight(Light _light)
        {
            _light.shadows = LightShadowType;
            if (!mi_sceneLights.Contains(_light))
            {
                mi_sceneLights.Add(_light);
            }
        }

        private void OnSceneLoaded(Scene _oldScene, Scene _newScene)
        {
            LoadLights();
        }

        #region PATCHES
        [HarmonyPatch(typeof(BlockCreator), "CreateBlock")]
        public class HarmonyPatch_BlockCreator_CreateBlock
        {
            //Intercept create block method so we can check each created block if it is a light
            [HarmonyPostfix]
            private static void BlockCreator_CreateBlock(BlockCreator __instance, Block __result)
            {
                if (__result == null) return;

                if (mi_sceneLights == null)
                {
                    mi_sceneLights = new List<Light>();
                }

                //check if a raft light controller is available (built-in torch)
                var ex = __result.GetComponent<LightSingularityExternal>();
                if (ex)
                {
                    RegisterLight(ex.MorphedLightSingularity.LightComponent);
                    return;
                }

                //if there is no raft light controller available try to search for other lights below the block to search for mod item lights
                var lights = __result.GetComponentsInChildren<Light>(true);
                if (lights.Length == 0) return;

                foreach (var light in lights)
                {
                    RegisterLight(light);
                }
            }
        }

        [HarmonyPatch(typeof(LightSingularity), "RecheckExternalConnection")]
        public class HarmonyPatch_LightSingularity_RecheckExternalConnection
        {
            //The recheck merges lights, need to prevent the method from executing.
            [HarmonyPrefix]
            private static bool LightSingularity_RecheckExternalConnection(LightSingularity __instance) => false;
        }

        [HarmonyPatch(typeof(LightSingularityExternal), "UpdateLightConnectivity")]
        public class HarmonyPatch_LightSingularityExternal_UpdateLightConnectivity
        {
            //do not morph light sources into singularity
            //Update light connectivity merges lights, need to prevent the method from executing.
            [HarmonyPrefix]
            private static bool LightSingularityExternal_UpdateLightConnectivity(LightSingularityExternal __instance) => false;
        }

        [HarmonyPatch(typeof(LightSingularityExternal), "ChooseWhichLightToBecome")]
        public class HarmonyPatch_LightSingularityExternal_ChooseWhichLightToBecome
        {
            [HarmonyPrefix]
            private static bool LightSingularityExternal_ChooseWhichLightToBecome(LightSingularityExternal __instance)
            {
                //create single singularity for each light
                var sManager = ComponentManager<LightSingularityManager>.Value;
                if (!__instance.MorphedLightSingularity)
                {
                    sManager.CreateNewSingularityFromMultiple(new[] { __instance.gameObject });
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(LightSingularityManager), "CheckToMergeSingularityWithOtherSingularities")]
        public class HarmonyPatch_LightSingularityManager_CheckToMergeSingularityWithOtherSingularities
        {
            [HarmonyPrefix]
            private static bool LightSingularityManager_CheckToMergeSingularityWithOtherSingularities() => false;
        }

        [HarmonyPatch(typeof(NightLightController), "Update")]
        public class HarmonyPatch_NightLightController_Update
        {
            //Do not switch off torch lights during daytime
            [HarmonyPrefix]
            private static bool NightLightController_Update() => !PreventDayNightLightSwitch;
        }

        [HarmonyPatch(typeof(Settings), "SaveAll")]
        public class HarmonyPatch_Settings_SaveAll
        {
            [HarmonyPostfix]
            private static void Settings_SaveAll()
            {
                LoadLights();   
            }
        }
        #endregion
    }
}