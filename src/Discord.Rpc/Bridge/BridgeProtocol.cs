namespace Discord.Rpc.Bridge
{
    /// <summary>
    /// Wire contract between the UWP widget and the full-trust bridge process, carried over
    /// an AppServiceConnection ValueSet.
    /// </summary>
    /// <remarks>
    /// The bridge exists because neither transport works from inside the widget's
    /// AppContainer: named pipes are blocked outright, and Discord rejects the RPC
    /// WebSocket with 4001 Invalid Origin unless the application has a configured
    /// rpc_origins allowlist, which the developer portal no longer exposes.
    ///
    /// The bridge runs outside the container, so it can use the named pipe that the
    /// console harness already proved works.
    ///
    /// Deliberately not a loopback socket: an unauthenticated local listener able to mute,
    /// deafen and move the user's Discord would be reachable by any process on the machine.
    /// AppService is scoped to this package.
    /// </remarks>
    public static class BridgeProtocol
    {
        /// <summary>Must match the AppService name declared in the package manifest.</summary>
        public const string AppServiceName = "DiscordRpcBridge";

        // ValueSet keys.
        public const string KeyCommand = "cmd";
        public const string KeyRequestId = "id";
        public const string KeySuccess = "ok";
        public const string KeyError = "error";
        public const string KeyPayload = "payload";
        public const string KeyEvent = "event";

        // Widget -> bridge.
        public const string CmdConnect = "connect";
        public const string CmdGetChannel = "getChannel";
        public const string CmdGetVoiceSettings = "getVoiceSettings";
        public const string CmdSetMuted = "setMuted";
        public const string CmdSetDeafened = "setDeafened";
        public const string CmdJoinChannel = "joinChannel";
        public const string CmdLeaveChannel = "leaveChannel";
        public const string CmdGetGuilds = "getGuilds";
        public const string CmdGetVoiceChannels = "getVoiceChannels";

        /// <summary>Reads the configured application id, for the settings widget to display.</summary>
        public const string CmdGetConfig = "getConfig";

        /// <summary>Writes the application id. Served even when nothing is configured yet.</summary>
        public const string CmdSetConfig = "setConfig";

        /// <summary>Drops the Discord session and connects again with the current config.</summary>
        public const string CmdReconnect = "reconnect";

        // Command arguments.
        public const string ArgValue = "value";
        public const string ArgChannelId = "channelId";
        public const string ArgGuildId = "guildId";
        public const string ArgClientId = "clientId";

        // Bridge -> widget, unsolicited.
        public const string EvtState = "state";
        public const string EvtChannel = "channel";
        public const string EvtSpeaking = "speaking";

        // Fields inside an event payload.
        public const string FieldCapabilities = "capabilities";
        public const string FieldCurrentUserId = "currentUserId";
    }
}
