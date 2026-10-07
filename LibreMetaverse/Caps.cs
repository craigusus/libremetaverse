/*
 * Copyright (c) 2006-2016, openmetaverse.co
 * Copyright (c) 2019-2026, Sjofn LLC.
 * All rights reserved.
 *
 * - Redistribution and use in source and binary forms, with or without
 *   modification, are permitted provided that the following conditions are met:
 *
 * - Redistributions of source code must retain the above copyright notice, this
 *   list of conditions and the following disclaimer.
 * - Neither the name of the openmetaverse.co nor the names
 *   of its contributors may be used to endorse or promote products derived from
 *   this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using LibreMetaverse.Interfaces;
using LibreMetaverse.Http;
using System.Net.Http;

namespace LibreMetaverse
{
    /// <summary>
    /// Capabilities is the name of the bidirectional HTTP REST protocol
    /// used to communicate non-real-time transactions such as teleporting or
    /// group messaging
    /// </summary>
    public partial class Caps
    {
        /// <summary>
        /// Triggered when an event is received via the EventQueueGet
        /// capability
        /// </summary>
        /// <param name="capsKey">Event name</param>
        /// <param name="message">Decoded event data</param>
        /// <param name="simulator">The simulator that generated the event</param>
        //public delegate void EventQueueCallback(string message, StructuredData.OSD body, Simulator simulator);

        public delegate void EventQueueCallback(string capsKey, IMessage message, Simulator simulator);

        /// <summary>Reference to the simulator this system is connected to</summary>
        public Simulator Simulator;

        internal Uri _SeedCapsURI;
        internal Dictionary<string, Uri> _Caps = new Dictionary<string, Uri>();

        private readonly CancellationTokenSource _HttpCts = new CancellationTokenSource();
        private EventQueueClient? _EventQueueClient = null;

        /// <summary>Outcome of the seed capability request</summary>
        internal enum SeedRequestState { Pending, Succeeded, Failed, Cancelled }

        /// <summary>Waits between seed request attempts: five attempts in total</summary>
        internal static readonly TimeSpan[] DefaultSeedRetryDelays =
        {
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)
        };

        private readonly TimeSpan[] _seedRetryDelays;
        private volatile SeedRequestState _seedState = SeedRequestState.Pending;

        /// <summary>Outcome of the seed capability request; Failed or Cancelled means no capabilities</summary>
        internal SeedRequestState SeedState => _seedState;

        /// <summary>The seed request; it always completes without throwing</summary>
        internal Task SeedRequest { get; }

        /// <summary>Capabilities URI this system was initialized with</summary>
        public Uri SeedCapsURI => _SeedCapsURI;

        /// <summary>Whether the capabilities event queue is connected and
        /// listening for incoming events</summary>
        public bool IsEventQueueRunning => _EventQueueClient != null && _EventQueueClient.Running;

        public EventQueueClient? EventQueue => _EventQueueClient;

        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="simulator"></param>
        /// <param name="seedcaps"></param>
        internal Caps(Simulator simulator, Uri seedcaps)
            : this(simulator, seedcaps, DefaultSeedRetryDelays)
        {
        }

        /// <param name="simulator"></param>
        /// <param name="seedcaps"></param>
        /// <param name="seedRetryDelays">Waits between seed request attempts (tests use short ones)</param>
        internal Caps(Simulator simulator, Uri seedcaps, TimeSpan[] seedRetryDelays)
        {
            Simulator = simulator;
            _SeedCapsURI = seedcaps;
            _seedRetryDelays = seedRetryDelays;

            SeedRequest = MakeSeedRequestAsync();
        }

        public void Disconnect(bool immediate)
        {
            Logger.Info($"Caps system for {Simulator} is {(immediate ? "aborting" : "disconnecting")}", Simulator.Client);

            DisposalHelper.SafeCancelAndDispose(_HttpCts, (m, e) => { if (e != null) Logger.Debug(m, e); else Logger.Debug(m); });
            _EventQueueClient?.Dispose();
        }

        /// <summary>
        /// Request the URI of a named capability
        /// </summary>
        /// <param name="capability">Name of the capability to request</param>
        /// <returns>The URI of the requested capability, or null if not found</returns>
        public Uri? CapabilityURI(string capability)
        {
            return _Caps.TryGetValue(capability, out var cap) ? cap : null;
        }

        /// <summary>
        /// Return the cap names.
        /// </summary>
        public Dictionary<string, Uri>.KeyCollection Capabilities()
        {
            return _Caps.Keys;
        }

        /// <summary>
        /// Useful for debugging, but not particularly a good idea
        /// </summary>
        /// <param name="cap">Capability address</param>
        /// <returns>Name of capability if it exists</returns>
        public string CapabilityNameFromURI(Uri cap)
        {
            return _Caps.First(x => x.Value == cap).Key;
        }

        /// <summary>
        /// Request preferred URI for texture fetch capability
        /// </summary>
        /// <returns>URI of preferred capability or null, or null if not found</returns>
        public Uri? GetTextureCapURI()
        {
            if (_Caps.TryGetValue("ViewerAsset", out var cap)) { return cap; }
            return _Caps.TryGetValue("GetTexture", out cap) ? cap : null;
        }

        /// <summary>
        /// Request preferred URI for object mesh fetch capability
        /// </summary>
        /// <returns>URI of preferred capability or null, or null if not found</returns>
        public Uri? GetMeshCapURI()
        {
            if (_Caps.TryGetValue("ViewerAsset", out var cap)) { return cap; }
            if (_Caps.TryGetValue("GetMesh2", out cap)) { return cap; }
            return _Caps.TryGetValue("GetMesh", out cap) ? cap : null;
        }
        public static OSDArray AllCapabilities = new OSDArray
        {
            "AbuseCategories",
            "AcceptFriendship",
            "AcceptGroupInvite",
            "AgentPreferences",
            "AgentProfile",
            "AgentState",
            "AttachmentResources",
            "AvatarPickerSearch",
            "AvatarRenderInfo",
            "CharacterProperties",
            "ChatSessionRequest",
            "CopyInventoryFromNotecard",
            "CreateInventoryCategory",
            "DeclineFriendship",
            "DeclineGroupInvite",
            "DispatchRegionInfo",
            "DirectDelivery",
            "EnvironmentSettings",
            "EstateAccess",
            "EstateChangeInfo",
            "EventQueueGet",
            "ExtEnvironment",
            "FetchLib2",
            "FetchLibDescendents2",
            "FetchInventory2",
            "FetchInventoryDescendents2",
            "IncrementCOFVersion",
            "RequestTaskInventory",
            "InterestList",
            "InventoryThumbnailUpload",
            "GetDisplayNames",
            "GetExperiences",
            "AgentExperiences",
            "FindExperienceByName",
            "GetExperienceInfo",
            "GetAdminExperiences",
            "GetCreatorExperiences",
            "ExperiencePreferences",
            "GroupExperiences",
            "UpdateExperience",
            "IsExperienceAdmin",
            "IsExperienceContributor",
            "RegionExperiences",
            "ExperienceQuery",
            "GetMesh",
            "GetMesh2",
            "GetMetadata",
            "GetObjectCost",
            "GetObjectPhysicsData",
            "GetTexture",
            "GroupAPIv1",
            "GroupMemberData",
            "GroupProposalBallot",
            "HomeLocation",
            "LandResources",
            "LSLSyntax",
            "MapLayer",
            "MapLayerGod",
            "MeshUploadFlag",
            "ModifyMaterialParams",
            "ModifyRegion",
            "NavMeshGenerationStatus",
            "NewFileAgentInventory",
            "NewFileAgentInventoryVariablePrice",
            "ObjectAnimation",
            "ObjectMedia",
            "ObjectMediaNavigate",
            "ObjectNavMeshProperties",
            "ParcelPropertiesUpdate",
            "ParcelVoiceInfoRequest",
            "ProductInfoRequest",
            "ProvisionVoiceAccountRequest",
            "ReadOfflineMsgs",
            "RegionObjects",
            "RegionSchedule",
            "RemoteParcelRequest",
            "RenderMaterials",
            "RequestTextureDownload",
            "ResourceCostSelected",
            "RetrieveNavMeshSrc",
            "SearchStatRequest",
            "SearchStatTracking",
            "SendPostcard",
            "SendUserReport",
            "SendUserReportWithScreenshot",
            "ServerReleaseNotes",
            "SetDisplayName",
            "SimConsoleAsync",
            "SimulatorFeatures",
            "StartGroupProposal",
            "TerrainNavMeshProperties",
            "TextureStats",
            "UntrustedSimulatorMessage",
            "UpdateAgentInformation",
            "UpdateAgentLanguage",
            "UpdateAvatarAppearance",
            "UpdateGestureAgentInventory",
            "UpdateGestureTaskInventory",
            "UpdateNotecardAgentInventory",
            "UpdateNotecardTaskInventory",
            "UpdateScriptAgent",
            "UpdateScriptTask",
            "UpdateSettingsAgentInventory",
            "UpdateSettingsTaskInventory",
            "UploadAgentProfileImage",
            "UpdateMaterialAgentInventory",
            "UpdateMaterialTaskInventory",
            "UploadBakedTexture",
            "UserInfo",
            "ViewerAsset",
            "ViewerBenefits",
            "ViewerMetrics",
            "ViewerStartAuction",
            "ViewerStats",
            "VoiceSignalingRequest",
            // AIS3
            "InventoryAPIv3",
            "LibraryAPIv3"
        };
        /// <summary>
        /// Requests the seed capability. Attempts run in a loop (never recursively), at most
        /// one more than there are retry delays. A 404 ends the request at once; a transport
        /// failure, timeout, other non-success status or unusable body is retried after the
        /// next delay. Cancellation (Disconnect) or a client disconnect ends it quietly.
        /// Capabilities are applied, and CapabilitiesReceived raised, only on success.
        /// </summary>
        private async Task MakeSeedRequestAsync()
        {
            if (Simulator == null || !Simulator.Client.Network.Connected)
            {
                _seedState = SeedRequestState.Cancelled;
                return;
            }

            CancellationToken token;
            try { token = _HttpCts.Token; }
            catch (ObjectDisposedException)
            {
                _seedState = SeedRequestState.Cancelled;
                return;
            }

            var attempts = _seedRetryDelays.Length + 1;
            for (var attempt = 1; ; attempt++)
            {
                if (token.IsCancellationRequested || !Simulator.Client.Network.Connected)
                {
                    SeedRequestCancelled();
                    return;
                }

                string failure;
                CapabilitiesFailureKind kind;
                int? httpStatus = null;
                string? exceptionType = null;
                try
                {
                    var (response, data) = await Simulator.Client.HttpCapsClient.PostAsync(_SeedCapsURI, OSDFormat.Xml, Caps.AllCapabilities, token);
                    httpStatus = (int)response.StatusCode;
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        SeedRequestFailed(token, CapabilitiesFailureKind.NotFound, attempt, httpStatus, null,
                            $"Seed capability for {Simulator} returned a 404, capability system is aborting");
                        return;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        kind = CapabilitiesFailureKind.HttpError;
                        failure = $"HTTP {(int)response.StatusCode}";
                    }
                    else if (TryParseSeedResponse(data, out var respMap, out failure, out exceptionType))
                    {
                        _seedState = SeedRequestState.Succeeded;
                        SeedRequestCompleteHandler(respMap);
                        return;
                    }
                    else
                    {
                        kind = CapabilitiesFailureKind.InvalidResponse;
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is ObjectDisposedException)
                {
                    if (token.IsCancellationRequested || ex is ObjectDisposedException)
                    {
                        SeedRequestCancelled();
                        return;
                    }
                    kind = CapabilitiesFailureKind.Timeout;
                    exceptionType = ex.GetType().Name;
                    failure = "timed out"; // HttpClient.Timeout, not our token
                }
                catch (Exception ex)
                {
                    kind = CapabilitiesFailureKind.Transport;
                    exceptionType = ex.GetType().Name;
                    failure = ex.GetType().Name;
                }

                if (attempt >= attempts)
                {
                    SeedRequestFailed(token, kind, attempt, httpStatus, exceptionType,
                        $"Seed capability request for {Simulator} failed after {attempt} attempts ({failure}); " +
                        "capabilities are unavailable");
                    return;
                }

                var delay = _seedRetryDelays[attempt - 1];
                Logger.Warn($"Seed capability request for {Simulator} failed (attempt {attempt} of {attempts}: {failure}); " +
                            $"retrying in {delay.TotalSeconds:0.###} s", Simulator.Client);
                try
                {
                    await Task.Delay(delay, token);
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is ObjectDisposedException)
                {
                    SeedRequestCancelled();
                    return;
                }
            }
        }

        private void SeedRequestCancelled()
        {
            _seedState = SeedRequestState.Cancelled;
            Logger.Debug($"Seed capability request for {Simulator} stopped: disconnected", Simulator.Client);
        }

        /// <summary>
        /// Terminal failure: no capabilities. Raises NetworkManager.CapabilitiesFailed once, unless
        /// this Caps was disconnected or the client went offline meanwhile (a deliberate stop is
        /// a cancellation, not a failure).
        /// </summary>
        private void SeedRequestFailed(CancellationToken token, CapabilitiesFailureKind kind, int attempts,
            int? httpStatus, string? exceptionType, string message)
        {
            if (token.IsCancellationRequested || !Simulator.Client.Network.Connected)
            {
                SeedRequestCancelled();
                return;
            }

            _seedState = SeedRequestState.Failed;
            Logger.Error(message, Simulator.Client);
            Simulator.Client.Network.RaiseCapabilitiesFailedEvent(
                new CapabilitiesFailedEventArgs(Simulator, kind, attempts, httpStatus, exceptionType));
        }

        /// <summary>
        /// Parses a successful seed response. False, with a short reason that never includes
        /// the body (it holds capability URLs), when the body is empty, not LLSD or not a map.
        /// </summary>
        private static bool TryParseSeedResponse(byte[]? responseData, out OSDMap respMap, out string failure,
            out string? exceptionType)
        {
            respMap = null!;
            exceptionType = null;
            if (responseData == null || responseData.Length == 0)
            {
                failure = "empty response";
                return false;
            }

            OSD result;
            try
            {
                result = OSDParser.Deserialize(responseData);
            }
            catch (Exception ex)
            {
                exceptionType = ex.GetType().Name;
                failure = $"invalid response ({ex.GetType().Name}, {responseData.Length} bytes)";
                return false;
            }

            if (result is OSDMap map)
            {
                respMap = map;
                failure = string.Empty;
                return true;
            }

            failure = $"invalid response ({result?.Type.ToString() ?? "null"}, {responseData.Length} bytes)";
            return false;
        }

        /// <summary>
        /// Applies a parsed seed response: registers the capabilities, starts the event queue
        /// and simulator features, then raises CapabilitiesReceived. Runs at most once per
        /// Caps; a failure here is logged and never retried.
        /// </summary>
        private void SeedRequestCompleteHandler(OSDMap respMap)
        {
            try
            {
                foreach (var cap in respMap.Keys)
                {
                    var maybeUri = respMap[cap]?.AsUri();
                    if (maybeUri != null)
                    {
                        _Caps[cap] = maybeUri;
                        Simulator.Client.CapsRateLimiter.RegisterCapUri(cap, maybeUri);
                    }
                }

                if (_Caps.TryGetValue("EventQueueGet", out var eventQueueGetCap))
                {
                    Logger.Trace($"Starting event queue for {Simulator}", Simulator.Client);

                    _EventQueueClient = new EventQueueClient(eventQueueGetCap, Simulator);
                    _EventQueueClient.OnConnected += EventQueueConnectedHandler;
                    _EventQueueClient.OnEvent += EventQueueEventHandler;
                    _EventQueueClient.Start();
                }

                if (_Caps.TryGetValue("SimulatorFeatures", out var simFeaturesCap))
                {
                    Logger.Trace($"Retrieving Simulator Features for {Simulator}", Simulator.Client);
                    Simulator.Features = new SimulatorFeatures(Simulator);
                    _ = FetchSimulatorFeaturesAsync(simFeaturesCap);
                }

                OnCapabilitiesReceived(Simulator);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error applying seed capability response for {Simulator}", ex, Simulator.Client);
            }
        }

        private async Task FetchSimulatorFeaturesAsync(Uri cap)
        {
            try
            {
                var (response, data) = await Simulator.Client.HttpCapsClient.GetAsync(cap, _HttpCts.Token);
                Simulator.Features.SetFeatures(response, data, null);
            }
            catch (Exception ex)
            {
                Simulator.Features.SetFeatures(null, null, ex);
            }
        }

        private void EventQueueConnectedHandler()
        {
            Simulator.Client.Network.RaiseConnectedEvent(Simulator);
        }

        /// <summary>
        /// Process any incoming events, check to see if we have a message created for the event,
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="body"></param>
        private void EventQueueEventHandler(string eventName, OSDMap body)
        {
            IMessage? message = Messages.MessageUtils.DecodeEvent(eventName, body);
            if (message != null)
            {
                Simulator.Client.Network.CapsEvents.BeginRaiseEvent(eventName, message, Simulator);

                #region Stats Tracking
                if (Simulator.Client.Settings.Packets.TrackUtilization)
                {
                    Simulator.Client.Stats.Update(eventName, LibreMetaverse.Stats.Type.Message, 0, body.ToString().Length);
                }
                #endregion
            }
            else
            {
                Logger.Warn($"No Message handler exists for event {eventName}. Unable to decode. Will try Generic Handler next");
                Logger.Debug("Please report this information at https://github.com/cinderblocks/libremetaverse/issues: " + Environment.NewLine + body);

                // try generic decoder next which takes a caps event and tries to match it to an existing packet
                if (body.Type == OSDType.Map)
                {
                    OSDMap map = body;
                    Packet? packet = Packet.BuildPacket(eventName, map);
                    if (packet != null)
                    {
                        var incomingPacket = new NetworkManager.IncomingPacket
                        {
                            Simulator = Simulator,
                            Packet = packet
                        };

                        Logger.DebugLog($"Serializing {packet.Type} capability with generic handler",
                            Simulator.Client);

                        Simulator.Client.Network.EnqueueIncoming(incomingPacket);
                    }
                    else
                    {
                        Logger.Warn($"No Packet or Message handler exists for {eventName}");
                    }
                }
            }
        }

        /// <summary>Raised whenever the capabilities have been received from a simulator</summary>
        public event EventHandler<CapabilitiesReceivedEventArgs>? CapabilitiesReceived;

        /// <summary>
        /// Raises the CapabilitiesReceived event
        /// </summary>
        /// <param name="simulator">Simulator we received the capabilities from</param>
        private void OnCapabilitiesReceived(Simulator simulator)
        {
            CapabilitiesReceived?.Invoke(this, new CapabilitiesReceivedEventArgs(simulator));
        }
    }

    #region EventArgs

    public class CapabilitiesReceivedEventArgs : EventArgs
    {
        /// <summary>The simulator that received a capabilities</summary>
        public Simulator Simulator { get; }

        public CapabilitiesReceivedEventArgs(Simulator simulator)
        {
            Simulator = simulator;
        }
    }

    #endregion
}

