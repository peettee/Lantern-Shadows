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

namespace pp.RaftMods.LanternShadows
{
    /// <summary>
    /// Enables realtime shadows on lantern buildables and other block lights.
    /// Allows lanterns and light sources (modded items as well) to be switched on and off.
    /// <see cref="CLanternSwitch"/> is the behaviour attached to a light sources collider (to intercept interaction ray) for interaction and takes care
    /// of controling light, particles effects and studio event emitters if a light source is switched off.
    /// Includes a configuration file stored at <see cref="Application.persistentDataPath"/> raft directory to alter the mod behaviour.
    /// </summary>
    public class CLanternShadows : Mod
    {
        private static CLanternShadows Get = null;

        public const string VERSION     = "1.1.0";
        public const string APP_NAME    = "LanternShadows";
        public const string APP_IDENT   = "pp.RaftMods." + APP_NAME;

        public static CModConfig Config { get; private set; }

        private string ModDataDirectory     => Path.Combine(Application.persistentDataPath, "Mods", APP_NAME);
        private string ModConfigFilePath    => Path.Combine(ModDataDirectory, "config.json");

        private static LightShadows LightShadowType => mi_settings.Current.graphics.shadowType == ShadowQuality.All ? LightShadows.Soft :
                                                        mi_settings.Current.graphics.shadowType == ShadowQuality.HardOnly ? LightShadows.Hard :
                                                            LightShadows.None;

        private static List<SSceneLight> mi_sceneLights;
        private static Settings mi_settings;

        private Harmony mi_harmony;

        /// <summary>
        /// Called when the mod is loaded.
        /// </summary>
        private void Start()
        {
            if (Get != null)
            {
                DestroyImmediate(Get);
                CUtil.LogW("Mod has been loaded twice. Destroying old mod instance.");
            }

            Get = this;

            LoadConfig();

            mi_harmony = new Harmony(APP_IDENT);
            mi_harmony.PatchAll(Assembly.GetExecutingAssembly());

            mi_settings = ComponentManager<Settings>.Value;

            if (RAPI.IsCurrentSceneGame())
            {
                LoadLights(); //mod was reloaded from game
            }

            CUtil.Log("LanternShadows v. " + VERSION + " loaded.");
        }

        /// <summary>
        /// Called when the mod is unloaded
        /// </summary>
        private void OnDestroy()
        {
            Get = null;

            //undo all changes to the scene
            if (mi_sceneLights != null)
            {
                foreach (var light in mi_sceneLights)
                {
                    RestoreLightSource(light);
                    if (light.LightSwitch)
                    {
                        Component.DestroyImmediate(light.LightSwitch);
                    }
                    if (light.Raycastable)
                    {
                        Component.DestroyImmediate(light.Raycastable);
                    }
                }
                mi_sceneLights = null;
            }

            if (mi_harmony != null)
            {
                mi_harmony.UnpatchAll(APP_IDENT);
            }

            mi_settings = null;
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
                if (!Directory.Exists(ModDataDirectory))
                {
                    Directory.CreateDirectory(ModDataDirectory);
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
                        }) ?? throw new System.Exception("Failed to serialize"));
            }
            catch (System.Exception _e)
            {
                CUtil.LogW("Failed to save mod configuration: " + _e.Message);
            }
        }

        //used for post-loading light sources into mod control. only used if mod is reloaded.
        private void LoadLights()
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

        //called when a light source is created or when reloading the mod
        private void RegisterLight(Light _light, GameObject _blockObject)
        {
            if (_light == null || _blockObject == null) return;

            if (mi_sceneLights == null)
            {
                mi_sceneLights = new List<SSceneLight>();
            }

            var block = _blockObject.GetComponent<Block>();
            if (!block) return;
            
            var sceneLight = new SSceneLight();

            sceneLight.BlockObject      = block;
            sceneLight.LightComponent   = _light;

            if (Config.EnableLightToggle)
            {
                var col = _blockObject.GetComponentInChildren<Collider>();
                if (!col) return;

                var raycastable = col.gameObject.GetComponent<RaycastInteractable>();
                if (!raycastable)
                {
                    raycastable = col.gameObject.AddComponent<RaycastInteractable>();
                }

                sceneLight.Raycastable = raycastable;

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
                sceneLight.LightSwitch.Load(this, sceneLight);
            }

            _light.shadows = Config.EnableShadows ? LightShadowType : LightShadows.None;
            if (!mi_sceneLights.Any(_o => _o.LightComponent == _light))
            {
                mi_sceneLights.Add(sceneLight);
            }
        }
       
        //called when settings are applied to update all lights shadows
        private void UpdateAllLightShadowType()
        {
            foreach(var light in mi_sceneLights)
            {
                light.LightComponent.shadows = Config.EnableShadows ? LightShadowType : LightShadows.None;
            }
        }

        /// <summary>
        /// Called when a block is spawned by the <see cref="BlockCreator"/> from the harmony patch <see cref="CHarmonyPatch_BlockCreator_CreateBlock"/>.
        /// Forwards to <see cref="RegisterLight(Light, GameObject)"/> if the created block was a light.
        /// </summary>
        /// <param name="_block">The created block.</param>
        private void OnBlockCreated(Block _block)
        {
            if (Get == null) return; //mod is being unloaded

            if (_block == null) return;

            if (mi_sceneLights == null)
            {
                mi_sceneLights = new List<SSceneLight>();
            }

            //check if a raft light controller is available (built-in torch)
            var ex = _block.GetComponent<LightSingularityExternal>();
            if (ex)
            {
                Get.RegisterLight(ex.MorphedLightSingularity.LightComponent, ex.gameObject);
                return;
            }

            //if there is no raft light controller available try to search for other lights below the block to search for mod item lights
            var lights = _block.GetComponentsInChildren<Light>(true);
            if (lights.Length == 0) return;

            foreach (var light in lights)
            {
                Get.RegisterLight(light, _block.gameObject);
            }
        }

        /// <summary>
        /// Called whenever a new light source attempts to create a singularity.
        /// By default, light sources are created as <see cref="LightSingularityExternal"/> which can be merged into <see cref="LightSingularity"/> to reduce performance impact of realtime lights.
        /// This mod prevents this step and creates a singularity for each external, meaning one <see cref="Light"/> for each lantern or modded light source.
        /// </summary>
        /// <param name="_lightSource">The light source searching trying to merge.</param>
        /// <returns>False to make sure the rest of the vanilla merge method is not executed.</returns>
        private bool OnLightSourceSpawn(LightSingularityExternal _lightSource)
        {
            if (Get == null) return true; //mod is being unloaded

            //create single singularity for each light
            var sManager = ComponentManager<LightSingularityManager>.Value;
            if (!_lightSource.MorphedLightSingularity)
            {
                sManager.CreateNewSingularityFromMultiple(new[] { _lightSource.gameObject });
            }
            return false;
        }

        /// <summary>
        /// Tries to restore each light source to its original vanilla state.
        /// Used on mod unload to make sure the game state is cleaned if the mod is disabled through the manager
        /// </summary>
        /// <param name="_lightSource"></param>
        internal void RestoreLightSource(SSceneLight _lightSource)
        {
            if (_lightSource.LightComponent) _lightSource.LightComponent.shadows = LightShadows.None;
            if (_lightSource.LightSwitch)
            {
                _lightSource.LightSwitch.SetLightOn(true);
            }
            if (_lightSource.PreModColliderLayer != 0)
            {
                if (_lightSource.Raycastable)
                {
                    _lightSource.Raycastable.gameObject.layer = _lightSource.PreModColliderLayer;
                }
            }
        }

        /// <summary>
        /// If a lantern object is destroyed by the player unregister it from our list. Its gone.
        /// </summary>
        /// <param name="_lightSource">The light source which was destroyed</param>
        internal void UnregisterLightSource(SSceneLight _lightSource)
        {
            if (Get == null) return; //mod is being unloaded

            if (mi_sceneLights?.Contains(_lightSource) ?? false)
            {
                mi_sceneLights.Remove(_lightSource);
            }
        }

        #region PATCHES
        [HarmonyPatch(typeof(BlockCreator), "CreateBlock")]
        public class CHarmonyPatch_BlockCreator_CreateBlock
        {
            //Intercept create block method so we can check each created block if it is a light
            [HarmonyPostfix]
            private static void BlockCreator_CreateBlock(BlockCreator __instance, Block __result) => Get.OnBlockCreated(__result);
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
            private static bool LightSingularityExternal_ChooseWhichLightToBecome(LightSingularityExternal __instance) => Get.OnLightSourceSpawn(__instance);
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
            private static void Settings_SaveAll() => Get.UpdateAllLightShadowType(); //after settings are saved by the player reload lights to make sure we update the shadow type
        }
        #endregion
    }

    [DisallowMultipleComponent] //disallow to really make sure we never get into the situation of components being added twice on mod reload.
    public class CLanternSwitch : MonoBehaviour_ID_Network, IRaycastable
    {
        /// <summary>
        /// True if the light source is currently switched on
        /// </summary>
        public bool IsOn { get; private set; }
        /// <summary>
        /// User currently controls this light source's state. Automatic day/night switch wont affect this source.
        /// </summary>
        public bool UserControlsState { get; set; }

        private CLanternShadows mi_mod;
        private CanvasHelper mi_canvas;
        private NightLightController mi_nlCntrl;
        private AzureSkyController mi_azureCntrl;
        private Semih_Network mi_network;

        private SSceneLight mi_sceneLight;
        private bool mi_isNight;

        private MethodInfo mi_setLightIntensityInfo;

        private bool mi_loaded = false;

        public void Load(CLanternShadows _mod, SSceneLight _light)
        {
            mi_mod                      = _mod;
            mi_sceneLight               = _light;
            mi_nlCntrl                  = mi_sceneLight.LightComponent.GetComponent<NightLightController>();
            mi_setLightIntensityInfo    = typeof(NightLightController).GetMethod("SetLightIntensity", BindingFlags.NonPublic | BindingFlags.Instance);
            mi_network                  = ComponentManager<Semih_Network>.Value;

            //use our block objects index so we receive RPC calls
            //need to use an existing blockindex as clients/host need to be aware of it
            ObjectIndex = mi_sceneLight.BlockObject.ObjectIndex;
            NetworkIDManager.AddNetworkID(this);

            CheckLightState(true);

            mi_loaded = true;

            if (!Semih_Network.IsHost) //request lantern states from host after load
            {
                mi_network.SendP2P(
                    mi_network.HostID,
                    new Message_Battery_OnOff( //just use the battery message as it should never
                        Messages.Battery_OnOff, 
                        mi_network.NetworkIDManager,
                        mi_network.LocalSteamID, 
                        this.ObjectIndex, 
                        (int)ELanternRequestType.REQUEST_STATE, //we use the battery uses int to pass our custom command type 
                        IsOn),
                    EP2PSend.k_EP2PSendReliable,
                    NetworkChannel.Channel_Game);
            }
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
                    mi_setLightIntensityInfo.Invoke(mi_nlCntrl, new object[] { IsOn ? 1f : 0f });
                }
                if ((mi_isNight != isNight || _forceSet) && !UserControlsState)
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
                mi_canvas.displayTextManager.ShowText(IsOn ? "Extinguish" : "Light", MyInput.Keybinds["Interact"].MainKey, 0, 0, true);
                if (MyInput.GetButtonDown("Interact"))
                {
                    UserControlsState = true;
                    SetLightOn(!IsOn);

                    var netMsg = new Message_Battery_OnOff(
                        Messages.Battery_OnOff,
                        RAPI.GetLocalPlayer().Network.NetworkIDManager,
                        RAPI.GetLocalPlayer().steamID,
                        ObjectIndex,
                        (int)ELanternRequestType.TOGGLE,
                        IsOn);

                    if (Semih_Network.IsHost)
                    {
                        mi_network.RPC(netMsg, Target.Other, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                        return;
                    }
                    mi_network.SendP2P(mi_network.HostID, netMsg, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                    return;
                }
                if (UserControlsState && Input.GetKeyDown(KeyCode.F))
                {
                    var notifier = ComponentManager<HNotify>.Value;
                    var notification = notifier.AddNotification(HNotify.NotificationType.normal, "Automatic light behaviour restored.", 5);
                    notification.Show();
                    UserControlsState = false;
                    CheckLightState(true);

                    var netMsg = new Message_Battery_OnOff(
                        Messages.Battery_OnOff,
                        RAPI.GetLocalPlayer().Network.NetworkIDManager,
                        RAPI.GetLocalPlayer().steamID,
                        ObjectIndex,
                        (int)ELanternRequestType.RELEASE_AUTO, //indicate to receiving side that we want to switch back to auto control
                        IsOn);

                    if (Semih_Network.IsHost)
                    {
                        mi_network.RPC(netMsg, Target.Other, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
                        return;
                    }
                    mi_network.SendP2P(mi_network.HostID, netMsg, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
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
            IsOn = _isLightOn;

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
            if (!mi_loaded) return base.Deserialize(_msg, _remoteID);

            Messages type = _msg.Type;
            if (_msg.Type != Messages.Battery_OnOff) return base.Deserialize(_msg, _remoteID);

            Message_Battery_OnOff msg = _msg as Message_Battery_OnOff;
            if (msg == null) return base.Deserialize(_msg, _remoteID);

            switch((ELanternRequestType)msg.batteryUsesLeft) //we use the usesleft value as our command type carrier
            {
                case ELanternRequestType.RELEASE_AUTO:
                    UserControlsState = false;
                    CheckLightState(true);
                    return true;
                case ELanternRequestType.REQUEST_STATE:  //a client block requested this blocks state, send it back
                    if (Semih_Network.IsHost)
                    {
                        if (!UserControlsState) return true;

                        mi_network.SendP2P(
                            _remoteID,
                            new Message_Battery_OnOff(Messages.Battery_OnOff, mi_network.NetworkIDManager, mi_network.LocalSteamID, this.ObjectIndex, (int)ELanternRequestType.TOGGLE, IsOn),
                            EP2PSend.k_EP2PSendReliable,
                            NetworkChannel.Channel_Game);
                    }
                    return true;
                case ELanternRequestType.TOGGLE:
                    UserControlsState = true;
                    SetLightOn((_msg as Message_Battery_OnOff)?.on ?? true);
                    return true;
            }
            return true;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            NetworkIDManager.RemoveNetworkID(this);
            mi_mod.UnregisterLightSource(mi_sceneLight);
        }
    }

    /// <summary>
    /// Wrapper class is used to manage scene lights and their connected components as well as storing meta info about each light.
    /// </summary>
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
        /// <summary>
        /// Prevents the day and night cykle to switch laterns off/on automatically.
        /// </summary>
        public bool PreventDayNightLightSwitch  ;
        /// <summary>
        /// Allow light sources to be interacted with.
        /// </summary>
        public bool EnableLightToggle           ;
        /// <summary>
        /// Turn particles and audio source off if lanterns are disabled.
        /// </summary>
        public bool TurnOffParticlesOnDisable   ;
        /// <summary>
        /// Enable realtime shadows on light sources.
        /// </summary>
        public bool EnableShadows               ;

        public CModConfig()
        {
            PreventDayNightLightSwitch  = false;
            EnableLightToggle           = true;
            TurnOffParticlesOnDisable   = true;
            EnableShadows               = true;
        }
    }

    public enum ELanternRequestType
    {
        RELEASE_AUTO    = -30,
        REQUEST_STATE   = -31,
        TOGGLE          = -32
    }

    public static class CUtil //util
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