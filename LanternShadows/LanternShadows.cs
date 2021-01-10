using FMODUnity;
using HarmonyLib;
using Newtonsoft.Json;
using Steamworks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.AzureSky;
using UnityEngine.SceneManagement;

namespace pp.RaftMods.LanternShadows
{
    /// <summary>
    /// Enables realtime shadows on lantern buildables and other block lights.
    /// </summary>
    public class CLanternShadows : Mod
    {
        public const string VERSION     = "1.1.0";
        public const string APP_NAME    = "LanternShadows";
        public const string APP_IDENT   = "pp.RaftMods." + APP_NAME;

        protected string ModConfigFilePath => Path.Combine(Application.persistentDataPath, "Mods", APP_NAME, "config.json");

        public static CModConfig Config { get; private set; }

        private static LightShadows LightShadowType => mi_settings.Current.graphics.shadowType == ShadowQuality.All ? LightShadows.Soft :
                                                        mi_settings.Current.graphics.shadowType == ShadowQuality.HardOnly ? LightShadows.Hard :
                                                            LightShadows.None;

        private static List<SSceneLight> mi_sceneLights;
        private static Settings mi_settings;

        private Harmony mi_harmony;

        private void Start()
        {
            LoadConfig();

            mi_harmony = new Harmony(APP_IDENT);
            mi_harmony.PatchAll(Assembly.GetExecutingAssembly());

            mi_settings = ComponentManager<Settings>.Value;

            SceneManager.activeSceneChanged -= OnSceneLoaded;
            SceneManager.activeSceneChanged += OnSceneLoaded;

            LoadLights();

            CUtil.Log("LanternShadows v. " + VERSION + " loaded.");
        }

        private void OnDestroy()
        {
            //undo all changes to the scene
            if (mi_sceneLights != null)
            {
                foreach (var light in mi_sceneLights)
                {
                    if (light.LightComponent) light.LightComponent.shadows = LightShadows.None;
                    if (light.LightSwitch)
                    {
                        light.LightSwitch.SetLightOn(true);
                        Object.DestroyImmediate(light.LightSwitch);
                    }
                    if (light.PreModColliderLayer != 0)
                    {
                        if (light.Raycastable)
                        {
                            light.Raycastable.gameObject.layer = light.PreModColliderLayer;
                        }
                    }
                    if (light.Raycastable) Component.DestroyImmediate(light.Raycastable);
                }
                mi_sceneLights = null;
            }

            if (mi_harmony != null)
            {
                mi_harmony.UnpatchAll(APP_IDENT);
            }

            mi_settings = null;

            SceneManager.activeSceneChanged -= OnSceneLoaded;
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ModConfigFilePath))
                {
                    SaveConfig();
                    return;
                }

                Config = JsonConvert.DeserializeObject<CModConfig>(File.ReadAllText(ModConfigFilePath)) ?? throw new System.Exception("Deserialisation failed.");
            }
            catch (System.Exception _e)
            {
                CUtil.LogW("Failed to load mod configuration: " + _e.Message + ". Check your configuration file.");
            }
        }

        private void SaveConfig()
        {
            try
            {
                var dir = Path.GetDirectoryName(ModConfigFilePath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if(Config == null)
                {
                    Config = new CModConfig();
                }

                File.WriteAllText(
                    ModConfigFilePath,
                    JsonConvert.SerializeObject(
                        Config,
                        Formatting.Indented,
                        new JsonSerializerSettings()
                        {
                            DefaultValueHandling    = DefaultValueHandling.Include
                        }));
            }
            catch (System.Exception _e)
            {
                CUtil.LogW("Failed to save mod configuration: " + _e.Message);
            }
        }

        private void OnSceneLoaded(Scene _oldScene, Scene _newScene)
        {
            LoadLights();
        }

        private static void LoadLights()
        {
            if (RAPI.IsCurrentSceneGame())
            {
                mi_sceneLights = new List<SSceneLight>();

                var raftLights      = GameObject.FindObjectsOfType<LightSingularity>();
                var otherLights     = GameObject.FindObjectsOfType<Light>()
                                                .Where(_o => _o.GetComponentInParent<Block>());

                foreach (var light in raftLights)
                {
                    RegisterLight(light.LightComponent, light.GetExternals()?.FirstOrDefault()?.gameObject);
                }
                foreach (var light in otherLights)
                {
                    RegisterLight(light, light.GetComponentInParent<Block>().gameObject);
                }
            }
        }

        private static void RegisterLight(Light _light, GameObject _blockObject)
        {
            if (_light == null || _blockObject == null) return;

            var block = _blockObject.GetComponent<Block>();
            if (!block) return;

            if (mi_sceneLights == null)
            {
                mi_sceneLights = new List<SSceneLight>();
            }

            var sceneLight = new SSceneLight();

            sceneLight.BlockObject      = block;
            sceneLight.LightComponent   = _light;

            if (Config.EnableLightToggle)
            {
                var col = _blockObject.GetComponentInChildren<Collider>();
                if (!col) return;

                if (!col.gameObject.GetComponent<RaycastInteractable>())
                {
                    sceneLight.Raycastable = col.gameObject.AddComponent<RaycastInteractable>();
                }

                if (col.gameObject.layer == 0)
                {
                    sceneLight.PreModColliderLayer  = col.gameObject.layer;
                    col.gameObject.layer    = LayerMask.NameToLayer("Block");
                }

                sceneLight.LightSwitch = col.gameObject.AddComponent<CLanternSwitch>();
            }
            else
            {
                sceneLight.LightSwitch = _light.gameObject.AddComponent<CLanternSwitch>();
            }

            if (sceneLight.LightSwitch)
            {
                sceneLight.BlockObject.networkedIDBehaviour = sceneLight.LightSwitch;
                sceneLight.LightSwitch.Load(sceneLight);
            }

            _light.shadows = Config.EnableShadows ? LightShadowType : LightShadows.None;
            if (!mi_sceneLights.Any(_o => _o.LightComponent == _light))
            {
                mi_sceneLights.Add(sceneLight);
            }
        }
       
        #region PATCHES
        [HarmonyPatch(typeof(BlockCreator), "CreateBlock")]
        public class CHarmonyPatch_BlockCreator_CreateBlock
        {
            //Intercept create block method so we can check each created block if it is a light
            [HarmonyPostfix]
            private static void BlockCreator_CreateBlock(BlockCreator __instance, Block __result)
            {
                if (__result == null) return;

                if (mi_sceneLights == null)
                {
                    mi_sceneLights = new List<SSceneLight>();
                }

                //check if a raft light controller is available (built-in torch)
                var ex = __result.GetComponent<LightSingularityExternal>();
                if (ex)
                {
                    RegisterLight(ex.MorphedLightSingularity.LightComponent, ex.gameObject);
                    return;
                }

                //if there is no raft light controller available try to search for other lights below the block to search for mod item lights
                var lights = __result.GetComponentsInChildren<Light>(true);
                if (lights.Length == 0) return;

                foreach (var light in lights)
                {
                    RegisterLight(light, __result.gameObject);
                }
            }
        }

        [HarmonyPatch(typeof(LightSingularity), "RecheckExternalConnection")]
        public class CHarmonyPatch_LightSingularity_RecheckExternalConnection
        {
            //The recheck merges lights, need to prevent the method from executing.
            [HarmonyPrefix]
            private static bool LightSingularity_RecheckExternalConnection(LightSingularity __instance) => false;
        }

        [HarmonyPatch(typeof(LightSingularityExternal), "UpdateLightConnectivity")]
        public class CHarmonyPatch_LightSingularityExternal_UpdateLightConnectivity
        {
            //do not morph light sources into singularity
            //Update light connectivity merges lights, need to prevent the method from executing.
            [HarmonyPrefix]
            private static bool LightSingularityExternal_UpdateLightConnectivity(LightSingularityExternal __instance) => false;
        }

        [HarmonyPatch(typeof(LightSingularityExternal), "ChooseWhichLightToBecome")]
        public class CHarmonyPatch_LightSingularityExternal_ChooseWhichLightToBecome
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
        public class CHarmonyPatch_LightSingularityManager_CheckToMergeSingularityWithOtherSingularities
        {
            [HarmonyPrefix]
            private static bool LightSingularityManager_CheckToMergeSingularityWithOtherSingularities() => false;
        }

        [HarmonyPatch(typeof(NightLightController), "Update")]
        public class CHarmonyPatch_NightLightController_Update
        {
            //Do not switch off torch lights during daytime
            [HarmonyPrefix]
            private static bool NightLightController_Update() => false;
        }

        [HarmonyPatch(typeof(Settings), "SaveAll")]
        public class CHarmonyPatch_Settings_SaveAll
        {
            [HarmonyPostfix]
            private static void Settings_SaveAll()
            {
                LoadLights(); //after settings are saved by the player reload lights to make sure we update the shadow type
            }
        }
        #endregion
    }

    [DisallowMultipleComponent]
    public class CLanternSwitch : MonoBehaviour_ID_Network, IRaycastable
    {
        private CanvasHelper mi_canvas;
        private NightLightController mi_nlCntrl;
        private AzureSkyController mi_azureCntrl;
        private Semih_Network mi_network;

        private SSceneLight mi_sceneLight;
        private bool mi_isOn;
        private bool mi_isNight;
        private bool mi_userSetState;

        private MethodInfo mi_setLightIntensityInfo;

        private bool mi_loaded = false;

        public void Load(SSceneLight _light)
        {
            mi_sceneLight               = _light;
            mi_nlCntrl                  = mi_sceneLight.LightComponent.GetComponent<NightLightController>();
            mi_setLightIntensityInfo    = typeof(NightLightController).GetMethod("SetLightIntensity", BindingFlags.NonPublic | BindingFlags.Instance);
            mi_loaded                   = true;
            mi_network                  = ComponentManager<Semih_Network>.Value;

            //use our block objects index so we receive RPC calls
            //need to use an existing blockindex as clients/host need to be aware of it
            ObjectIndex = mi_sceneLight.BlockObject.ObjectIndex;
            NetworkIDManager.AddNetworkID(this);

            CheckLightState(true);
        }

        private void Update()
        {
            if (!mi_loaded) return;

            CheckLightState(false);
        }

        private void CheckLightState(bool _forceSet)
        {
            if (mi_azureCntrl == null)
            {
                mi_azureCntrl = ComponentManager<AzureSkyController>.Value;
            }

            var isNight =   mi_azureCntrl.timeOfDay.hour > Traverse.Create(mi_nlCntrl).Field("nightTimeStart").GetValue<float>() ||
                            mi_azureCntrl.timeOfDay.hour < Traverse.Create(mi_nlCntrl).Field("nightTimeEnd").GetValue<float>();

            if (!CLanternShadows.Config.PreventDayNightLightSwitch)
            {
                if (mi_setLightIntensityInfo != null && mi_nlCntrl != null)
                {
                    mi_setLightIntensityInfo.Invoke(mi_nlCntrl, new object[] { mi_isOn ? 1f : 0f });
                }
                if ((mi_isNight != isNight || _forceSet) && !mi_userSetState)
                {
                    mi_isNight = isNight;
                    SetLightOn(mi_isNight);
                }
            }
        }

        public void OnIsRayed()
        {
            if (!mi_loaded) return;

            if (!mi_canvas)
            {
                mi_canvas = ComponentManager<CanvasHelper>.Value;
                return;
            }

            if (CanvasHelper.ActiveMenu != MenuType.None)
            {
                mi_canvas.displayTextManager.HideDisplayTexts();
                return;
            }

            if (!PlayerItemManager.IsBusy && mi_canvas.CanOpenMenu && Helper.LocalPlayerIsWithinDistance(transform.position, Player.UseDistance + 0.5f))
            {
                mi_canvas.displayTextManager.ShowText(mi_isOn ? "Extinguish" : "Light", MyInput.Keybinds["Interact"].MainKey, 0, 0, true);
                if (MyInput.GetButtonDown("Interact"))
                {
                    mi_userSetState = true;
                    SetLightOn(!mi_isOn);

                    var netMsg = new Message_Battery_OnOff(
                        Messages.Battery_OnOff,
                        RAPI.GetLocalPlayer().Network.NetworkIDManager,
                        RAPI.GetLocalPlayer().steamID,
                        ObjectIndex,
                        1,
                        mi_isOn);

                    if (Semih_Network.IsHost)
                    {
                        mi_network.RPC(netMsg, Target.Other, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                        return;
                    }
                    mi_network.SendP2P(mi_network.HostID, netMsg, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                    return;
                }
            }
            else
            {
                mi_canvas.displayTextManager.HideDisplayTexts();
            }
        }

        public void OnRayEnter()
        {
        }

        public void OnRayExit()
        {
            if (mi_canvas != null && mi_canvas.displayTextManager != null)
            {
                mi_canvas.displayTextManager.HideDisplayTexts();
            }
        }

        public void SetLightOn(bool _isLightOn)
        {
            mi_isOn = _isLightOn;

            if (!mi_sceneLight.BlockObject) return;

            var ps = mi_sceneLight.BlockObject.GetComponentInChildren<ParticleSystem>();
            var se = mi_sceneLight.BlockObject.GetComponentInChildren<StudioEventEmitter>();

            if (CLanternShadows.Config.TurnOffParticlesOnDisable)
            {
                if (_isLightOn)
                {
                    if (ps) ps.Play(true);
                    if (se) se.Play();
                }
                else
                {
                    if (ps) ps.Stop(true);
                    if (se) se.Stop();
                }
            }
            mi_sceneLight.LightComponent.enabled = _isLightOn;
        }

        public override bool Deserialize(Message_NetworkBehaviour _msg, CSteamID _remoteID)
        {
            CUtil.Log("Network message received from  " + _remoteID.m_SteamID.ToString() + ": " + _msg.Type);
            Messages type = _msg.Type;
            if (_msg.Type == Messages.Battery_OnOff)
            {
                SetLightOn((_msg as Message_Battery_OnOff)?.on ?? true);
                return true;
            }
            return base.Deserialize(_msg, _remoteID);
        }

        protected override void OnDestroy()
        {
            NetworkIDManager.RemoveNetworkID(this);
            base.OnDestroy();
        }
    }

    public struct SSceneLight
    {
        public Light LightComponent;
        public CLanternSwitch LightSwitch;
        public Block BlockObject;
        public RaycastInteractable Raycastable;
        public int PreModColliderLayer;
    }

    [System.Serializable]
    public class CModConfig
    {
        public bool PreventDayNightLightSwitch  ;
        public bool EnableLightToggle           ;
        public bool TurnOffParticlesOnDisable   ;
        public bool EnableShadows               ;

        public CModConfig()
        {
            PreventDayNightLightSwitch  = false;
            EnableLightToggle           = true;
            TurnOffParticlesOnDisable   = true;
            EnableShadows               = true;
        }
    }

    public static class CUtil
    {
        public static void Log(object _message)
        {
            Debug.Log($"[{CLanternShadows.APP_NAME}] {_message}");
        }

        public static void LogW(object _message)
        {
            Debug.LogWarning($"[{CLanternShadows.APP_NAME}] {_message}");
        }

        public static void LogE(object _message)
        {
            Debug.LogError($"[{CLanternShadows.APP_NAME}] {_message}");
        }
    }
}