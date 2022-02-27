﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using HADotNet.Core;
using HADotNet.Core.Clients;
using HASSAgent.Enums;
using HASSAgent.Functions;
using HASSAgent.Models.HomeAssistant;
using HASSAgent.Models.Internal;
using HASSAgent.Sensors;
using Serilog;

namespace HASSAgent.HomeAssistant
{
    /// <summary>
    /// Uses HASS's API to fetch entities, their status and to execute actions (on, off, etc) 
    /// </summary>
    internal static class HassApiManager
    {
        private static ConfigClient _configClient;
        private static ServiceClient _serviceClient;
        private static EntityClient _entityClient;
        private static StatesClient _statesClient = null;
        
        internal static HassManagerStatus ManagerStatus = HassManagerStatus.Initialising;
        private static string _haVersion = string.Empty;

        internal static List<string> AutomationList = new List<string>();
        internal static List<string> ScriptList = new List<string>();
        internal static List<string> InputBooleanList = new List<string>();
        internal static List<string> SceneList = new List<string>();
        internal static List<string> SwitchList = new List<string>();
        internal static List<string> LightList = new List<string>();
        internal static List<string> CoverList = new List<string>();
        internal static List<string> ClimateList = new List<string>();
        internal static List<string> MediaPlayerList = new List<string>();

        private static readonly string[] OnStates = { "on", "playing", "open", "opening" };
        private static readonly string[] OffStates = { "off", "idle", "paused", "stopped", "closed", "closing" };

        /// <summary>
        /// Initializes the HASS API manager, establishes a connection and loads the entities
        /// </summary>
        /// <returns></returns>
        internal static async Task<HassManagerStatus> InitializeAsync()
        {
            try
            {
                // do we have the required settings?
                if (!CheckSettings())
                {
                    ManagerStatus = HassManagerStatus.ConfigMissing;
                    Variables.MainForm?.SetHassApiStatus(ComponentStatus.Stopped);
                    return ManagerStatus;
                }

                // initialize hass client, optionally using certificate
                var clientInitialized = InitializeClient();
                if (!clientInitialized)
                {
                    Variables.MainForm?.SetHassApiStatus(ComponentStatus.Failed);
                    ManagerStatus = HassManagerStatus.Failed;

                    Variables.MainForm?.ShowToolTip("hass api: connection setup failed", true);
                    return ManagerStatus;
                }

                // retrieve config
                if (!await GetConfig())
                {
                    Variables.MainForm?.ShowToolTip("hass api: initial connection failed", true);
                    return ManagerStatus;
                }

                // prepare clients
                _serviceClient = ClientFactory.GetClient<ServiceClient>();
                _entityClient = ClientFactory.GetClient<EntityClient>();
                _statesClient = ClientFactory.GetClient<StatesClient>();

                // load entities
                ManagerStatus = HassManagerStatus.LoadingData;
                await LoadEntitiesAsync();

                // start periodic state retriever
                _ = Task.Run(PeriodicStatusUpdates);

                // start periodic entity reloading
                _ = Task.Run(PeriodicEntityReload);

                // done
                Log.Information("[HASS_API] System connected with {ip}", Variables.AppSettings.HassUri);
                Variables.MainForm?.SetHassApiStatus(ComponentStatus.Ok);

                ManagerStatus = HassManagerStatus.Ready;
                return ManagerStatus;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[HASS_API] Error while initializing: {err}", ex.Message);

                Variables.MainForm?.SetHassApiStatus(ComponentStatus.Failed);
                ManagerStatus = HassManagerStatus.Failed;

                Variables.MainForm?.ShowToolTip("hass api: connection failed", true);
                return ManagerStatus;
            }
        }

        /// <summary>
        /// Initializes the HA API client, optionally using provided certificate config
        /// </summary>
        /// <returns></returns>
        private static bool InitializeClient()
        {
            try
            {
                var hassUri = new Uri(Variables.AppSettings.HassUri);

                // automatic certificate selection
                if (Variables.AppSettings.HassAutoClientCertificate)
                {
                    Log.Information("[HASS_API] Connecting using automatic client certificate selection");

                    var handler = new HttpClientHandler();
                    handler.ClientCertificateOptions = ClientCertificateOption.Automatic;

                    ClientFactory.Initialize(hassUri, Variables.AppSettings.HassToken, handler);
                    return true;
                }

                // manual certificate selection
                if (!string.IsNullOrEmpty(Variables.AppSettings.HassClientCertificate))
                {
                    if (!File.Exists(Variables.AppSettings.HassClientCertificate))
                    {
                        Log.Error("[HASS_API] The specified certificate isn't found: {cert}", Variables.AppSettings.HassClientCertificate);
                        return false;
                    }

                    var certFile = Path.GetFileName(Variables.AppSettings.HassClientCertificate);
                    Log.Information("[HASS_API] Connecting using client certificate: {cert}", certFile);

                    var handler = new HttpClientHandler();
                    handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                    handler.ClientCertificates.Add(new X509Certificate2(Variables.AppSettings.HassClientCertificate));

                    ClientFactory.Initialize(hassUri, Variables.AppSettings.HassToken, handler);
                    return true;
                }

                // default connection
                ClientFactory.Initialize(hassUri, Variables.AppSettings.HassToken);
                return true;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[HASS_API] Error while initializing client: {err}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Fetches Home Assistant's config, will keep retrying in a 60 seconds period
        /// </summary>
        /// <returns></returns>
        private static async Task<bool> GetConfig()
        {
            // prepare a stopwatch to time our execution
            var runningTimer = Stopwatch.StartNew();
            Exception err = null;

            // prepare a config client
            _configClient = ClientFactory.GetClient<ConfigClient>();

            // start trying during the grace period
            while (runningTimer.Elapsed.Seconds < Variables.AppSettings.DisconnectedGracePeriodSeconds)
            {
                try
                {
                    // attempt to fetch the config
                    var config = await _configClient.GetConfiguration();

                    // if we're here, the connection works
                    if (config.Version == _haVersion) return true;

                    // version changed since last check (or this is the first check), log
                    _haVersion = config.Version;
                    Log.Information("[HASS_API] Home Assistant version: {version}", config.Version);
                    return true;
                }
                catch (Exception ex)
                {
                    if (err == null)
                    {
                        // set state to loading on the first failed attempt
                        Variables.MainForm?.SetHassApiStatus(ComponentStatus.Connecting);
                        ManagerStatus = HassManagerStatus.Initialising;
                    }

                    // save the exception
                    err = ex;

                    // wait a bit
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }

            // if we're here, set failed state and log
            Variables.MainForm?.SetHassApiStatus(ComponentStatus.Failed);
            ManagerStatus = HassManagerStatus.Failed;

            if (err != null) Log.Fatal("[HASS_API] Error while fetching HA config: {err}", err.Message);
            else Log.Error("[HASS_API] Error while fetching HA config: timeout");
            return false;
        }

        /// <summary>
        /// Checks if the connection's working, if not, will retry for max 60 seconds through GetConfig()
        /// </summary>
        /// <returns></returns>
        private static async Task<bool> CheckConnection()
        {
            // check if we can connect
            if (!await GetConfig()) return false;

            // optionally reset failed state
            if (ManagerStatus == HassManagerStatus.Failed)
            {
                // reset failed state and log
                ManagerStatus = HassManagerStatus.Ready;
                Variables.MainForm?.SetHassApiStatus(ComponentStatus.Ok);

                Log.Information("[HASS_API] Server recovered from failed state");

                // reset all sensors so they'll republish
                SensorsManager.ResetAllSensorChecks();
            }

            // all good
            return true;
        }

        /// <summary>
        /// Checks the provided provided credentials to see if we can connect
        /// <para>This will disconnect any current connections!</para>
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="apiKey"></param>
        /// <param name="automaticClientCertificate"></param>
        /// <param name="clientCertificate"></param>
        /// <returns></returns>
        internal static async Task<(bool success, string message)> CheckHassConfigAsync(string uri, string apiKey, bool automaticClientCertificate = false, string clientCertificate = "")
        {
            try
            {
                // optionally reset the client
                if (ClientFactory.IsInitialized) ClientFactory.Reset();

                // initialize hass client, optionally using certificate
                if (automaticClientCertificate)
                {
                    // automatic certificate selection
                    var handler = new HttpClientHandler();
                    handler.ClientCertificateOptions = ClientCertificateOption.Automatic;

                    ClientFactory.Initialize(uri, apiKey, handler);
                }
                else if (!string.IsNullOrEmpty(clientCertificate))
                {
                    // manual certificate selection
                    if (!File.Exists(clientCertificate)) return (false, "client certificate file not found");

                    var handler = new HttpClientHandler();
                    handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                    handler.ClientCertificates.Add(new X509Certificate2(clientCertificate));

                    ClientFactory.Initialize(uri, apiKey, handler);
                }
                else ClientFactory.Initialize(uri, apiKey);
                
                // check if we're initialized
                if (!ClientFactory.IsInitialized) return (false, "unable to connect, check uri");

                // check if we can fetch config
                _configClient = ClientFactory.GetClient<ConfigClient>();
                var config = await _configClient.GetConfiguration();
                if (config == null) return (false, "unable to fetch config, check api key");

                // looks ok
                return (true, config.Version);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[HASS_API] Error while checking config: {err}", ex.Message);
                return (false, "unable to connect, check uri and config");
            }
            finally
            {
                // reset if we're intialised
                if (ClientFactory.IsInitialized) ClientFactory.Reset();
            }
        }

        /// <summary>
        /// Check whether the HASS config is available
        /// </summary>
        /// <returns></returns>
        private static bool CheckSettings()
        {
            // todo: check data values
            if (!string.IsNullOrEmpty(Variables.AppSettings.HassUri) && !string.IsNullOrEmpty(Variables.AppSettings.HassToken)) return true;

            return false;
        }

        /// <summary>
        /// Fetches all entities from HASS
        /// </summary>
        /// <returns></returns>
        private static async Task LoadEntitiesAsync(bool clearCurrent = false)
        {
            if (clearCurrent)
            {
                // clear current lists
                AutomationList.Clear();
                ScriptList.Clear();
                InputBooleanList.Clear();
                SceneList.Clear();
                SwitchList.Clear();
                LightList.Clear();
            }

            try
            {
                var domain = "automation";
                var entities = await _entityClient.GetEntities(domain);
                foreach (var automation in entities)
                {
                    AutomationList.Add(automation.Remove(0, domain.Length + 1));
                }

                domain = "script";
                entities = await _entityClient.GetEntities(domain);
                foreach (var script in entities)
                {
                    ScriptList.Add(script.Remove(0, domain.Length + 1));
                }

                domain = "input_boolean";
                entities = await _entityClient.GetEntities(domain);
                foreach (var inputboolean in entities)
                {
                    InputBooleanList.Add(inputboolean.Remove(0, domain.Length + 1));
                }

                domain = "scene";
                entities = await _entityClient.GetEntities(domain);
                foreach (var scene in entities)
                {
                    SceneList.Add(scene.Remove(0, domain.Length + 1));
                }

                domain = "switch";
                entities = await _entityClient.GetEntities(domain);
                foreach (var @switch in entities)
                {
                    SwitchList.Add(@switch.Remove(0, domain.Length + 1));
                }

                domain = "light";
                entities = await _entityClient.GetEntities(domain);
                foreach (var light in entities)
                {
                    LightList.Add(light.Remove(0, domain.Length + 1));
                }

                domain = "cover";
                entities = await _entityClient.GetEntities(domain);
                foreach (var cover in entities)
                {
                    CoverList.Add(cover.Remove(0, domain.Length + 1));
                }

                domain = "climate";
                entities = await _entityClient.GetEntities(domain);
                foreach (var climate in entities)
                {
                    ClimateList.Add(climate.Remove(0, domain.Length + 1));
                }

                domain = "media_player";
                entities = await _entityClient.GetEntities(domain);
                foreach (var mediaplayer in entities)
                {
                    MediaPlayerList.Add(mediaplayer.Remove(0, domain.Length + 1));
                }

                if (ManagerStatus != HassManagerStatus.Failed) return;

                // reset failed state and log
                ManagerStatus = HassManagerStatus.Ready;
                Variables.MainForm?.SetHassApiStatus(ComponentStatus.Ok);

                Log.Information("[HASS_API] Server recovered from failed state");
            }
            catch (Exception ex)
            {
                // only log errors once to prevent log spamming
                if (ManagerStatus == HassManagerStatus.Failed) return;

                // set failed state and log
                Variables.MainForm?.SetHassApiStatus(ComponentStatus.Failed);
                ManagerStatus = HassManagerStatus.Failed;

                Log.Error("[HASS_API] Error while reloading entities: {err}", ex.Message);
            }
        }

        /// <summary>
        /// Executes the quick action
        /// </summary>
        /// <param name="quickAction"></param>
        /// <returns></returns>
        internal static async Task ProcessQuickActionAsync(QuickAction quickAction)
        {
            await ProcessActionAsync(quickAction.ToHassEntity(), quickAction.Action);
        }

        /// <summary>
        /// Executes the desired action on the entity
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        internal static async Task<bool> ProcessActionAsync(HassEntity entity, HassAction action)
        {
            var actionVal = action.GetDescription();
            var domainVal = entity.Domain.GetDescription();
            var entityVal = entity.Entity.ToLower();

            try
            {
                // check if the states client is up
                if (_statesClient == null)
                {
                    Log.Error("[HASS_API] [{domain}.{entity}] Unable to execute action, states client not initialized", domainVal, entityVal);
                    Variables.MainForm?.ShowToolTip("quick action: action failed, check the logs for info", true);
                    return false;
                }

                Log.Information("[HASS_API] [{domain}.{entity}] Performing action: {action}", domainVal, entityVal, actionVal);

                var fullEntity = $"{domainVal}.{entityVal}";

                // if a toggle is requested, we need to know its current state
                if (action == HassAction.Toggle)
                {
                    // try to find the entity
                    var state = await _statesClient.GetState(fullEntity);

                    // toggle based on state
                    if (OnStates.Contains(state.State))
                    {
                        Log.Information("[HASS_API] [{domain}.{entity}] Entity currently ON, changing action to 'turn_off'", domainVal, entityVal);
                        action = HassAction.Off;
                        actionVal = action.GetDescription();
                    }
                    else if (OffStates.Contains(state.State))
                    {
                        Log.Information("[HASS_API] [{domain}.{entity}] Entity currently OFF, changing action to 'turn_on'", domainVal, entityVal);
                        action = HassAction.On;
                        actionVal = action.GetDescription();
                    }
                    else
                    {
                        Log.Information("[HASS_API] [{domain}.{entity}] Entity in unknown state ({state}), defaulting to 'turn_on'", domainVal, entityVal, state.State);
                        action = HassAction.On;
                        actionVal = action.GetDescription();
                    }
                }

                // determine service
                var service = DetermineServiceForDomain(entity.Domain, action);

                // process the request
                _ = await _serviceClient.CallService(service, $@"{{""entity_id"":""{fullEntity}""}}");

                // done
                Log.Information("[HASS_API] [{domain}.{entity}] Action completed: {action}", domainVal, entityVal, actionVal);
                return true;
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("code 404"))
                {
                    Log.Error("[HASS_API] [{domain}.{entity}] Error while processing action: entity not found", domainVal, entityVal);
                    Variables.MainForm?.ShowToolTip("quick action: action failed, entity not found", true);
                    return false;
                }

                Log.Fatal(ex, "[HASS_API] [{domain}.{entity}] Error while processing action: {ex}", domainVal, entityVal, ex.Message);
                Variables.MainForm?.ShowToolTip("quick action: action failed, check the logs for info", true);
                return false;
            }
        }

        /// <summary>
        /// Periodically fetches all entities
        /// </summary>
        private static async void PeriodicEntityReload()
        {
            while (!Variables.ShuttingDown)
            {
                // wait a while
                await Task.Delay(TimeSpan.FromMinutes(5));

                // check if the connection's still up
                if (!await CheckConnection()) return;

                // reload all entities
                await LoadEntitiesAsync(true);
            }
        }

        /// <summary>
        /// Periodically gets the status of all the QuickActions
        /// <para>If we don't do this, it takes 10 seconds to get the state after idleing a while</para>
        /// </summary>
        private static async void PeriodicStatusUpdates()
        {
            while (!Variables.ShuttingDown)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));

                // check if the connection's still up
                if (!await CheckConnection()) continue;

                foreach (var quickAction in Variables.QuickActions)
                {
                    try
                    {
                        var entity = quickAction.ToHassEntity();

                        var domainVal = entity.Domain.GetDescription();
                        var entityVal = entity.Entity.ToLower();
                        var fullEntity = $"{domainVal}.{entityVal}";

                        _ = await _statesClient.GetState(fullEntity);

                        if (Variables.ShuttingDown) return;
                        if (ManagerStatus != HassManagerStatus.Failed) continue;

                        // reset failed state and log
                        ManagerStatus = HassManagerStatus.Ready;
                        Variables.MainForm?.SetHassApiStatus(ComponentStatus.Ok);

                        Log.Information("[HASS_API] Server recovered from failed state");
                    }
                    catch (HttpRequestException ex)
                    {
                        if (Variables.ShuttingDown) return;

                        if (ex.Message.Contains("404"))
                        {
                            Log.Warning("[HASS_API] Server returned 404 (not found) while getting entity state. This can happen after a server reboot, or if you've deleted the entity. If the problem persists, please file a ticket on github.\r\nEntity: {entity}\r\nError message: {err}", $"{quickAction.Domain}.{quickAction.Entity}", ex.Message);
                            return;
                        }

                        // only log errors once to prevent log spamming
                        if (ManagerStatus == HassManagerStatus.Failed) continue;

                        // set failed state and log
                        Variables.MainForm?.SetHassApiStatus(ComponentStatus.Failed);
                        ManagerStatus = HassManagerStatus.Failed;

                        Log.Error("[HASS_API] HTTP error while getting periodic status update: {err}", ex.Message);
                    }
                    catch (Exception ex)
                    {
                        if (Variables.ShuttingDown) return;
                        if (ManagerStatus == HassManagerStatus.Failed) continue;

                        // set failed state and log
                        Variables.MainForm?.SetHassApiStatus(ComponentStatus.Failed);
                        ManagerStatus = HassManagerStatus.Failed;

                        Log.Fatal(ex, "[HASS_API] Error while getting periodic status update: {err}", ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// Determines the full service call for the domain and action
        /// </summary>
        /// <param name="domain"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        private static string DetermineServiceForDomain(HassDomain domain, HassAction action)
        {
            var domainValue = domain.GetDescription();

            // attempt to fix some impossible settings
            switch (domain)
            {
                case HassDomain.Cover when action == HassAction.On:
                    action = HassAction.Open;
                    break;
                case HassDomain.Cover when action == HassAction.Off:
                    action = HassAction.Close;
                    break;
                case HassDomain.MediaPlayer when action == HassAction.On:
                    action = HassAction.Play;
                    break;
                case HassDomain.MediaPlayer when action == HassAction.Off:
                    action = HassAction.Stop;
                    break;
            }
            
            var actionValue = action.GetDescription();
            return $"{domainValue}.{actionValue}";
        }
    }
}
