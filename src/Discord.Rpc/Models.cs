using System;
using System.Collections.Generic;

namespace Discord.Rpc
{
    /// <summary>
    /// What a given session implementation can actually do. The RPC session supports
    /// everything; a WebView2-based fallback would report only <see cref="None"/>,
    /// since an embedded web client exposes no programmatic voice surface.
    /// The widget UI should hide controls rather than call unsupported operations.
    /// </summary>
    [Flags]
    public enum SessionCapabilities
    {
        None = 0,
        ReadVoiceState = 1 << 0,
        SetVoiceState = 1 << 1,
        SpeakingEvents = 1 << 2,
        ChannelNavigation = 1 << 3,

        Full = ReadVoiceState | SetVoiceState | SpeakingEvents | ChannelNavigation,
    }

    public enum SessionState
    {
        Disconnected,
        Connecting,
        Connected,
        /// <summary>Connected to Discord, but the app lacks approved RPC scopes.</summary>
        Unauthorized,
        Faulted,
    }

    public sealed class VoiceUser
    {
        public string Id { get; }
        public string Username { get; }
        public string? Nickname { get; }
        public bool IsMuted { get; }
        public bool IsDeafened { get; }

        /// <summary>
        /// Fully resolved CDN URL, never null: users without an avatar resolve to their
        /// default one. Built here rather than in the UI so the widget and the bridge
        /// cannot disagree about it.
        /// </summary>
        public string AvatarUrl { get; }

        public string DisplayName => string.IsNullOrEmpty(Nickname) ? Username : Nickname!;

        public VoiceUser(string id, string username, string? nickname, bool isMuted, bool isDeafened, string avatarUrl)
        {
            Id = id;
            Username = username;
            Nickname = nickname;
            IsMuted = isMuted;
            IsDeafened = isDeafened;
            AvatarUrl = avatarUrl;
        }
    }

    public sealed class VoiceChannelSnapshot
    {
        public string Id { get; }
        public string Name { get; }
        public string? GuildId { get; }
        public IReadOnlyList<VoiceUser> Participants { get; }

        public VoiceChannelSnapshot(string id, string name, string? guildId, IReadOnlyList<VoiceUser> participants)
        {
            Id = id;
            Name = name;
            GuildId = guildId;
            Participants = participants;
        }
    }

    public sealed class GuildSummary
    {
        public string Id { get; }
        public string Name { get; }

        public GuildSummary(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }

    public sealed class VoiceChannelSummary
    {
        public string Id { get; }
        public string Name { get; }
        public string GuildId { get; }

        public VoiceChannelSummary(string id, string name, string guildId)
        {
            Id = id;
            Name = name;
            GuildId = guildId;
        }
    }

    public sealed class LocalVoiceSettings
    {
        public bool IsMuted { get; }
        public bool IsDeafened { get; }

        public LocalVoiceSettings(bool isMuted, bool isDeafened)
        {
            IsMuted = isMuted;
            IsDeafened = isDeafened;
        }
    }

    public sealed class SpeakingEventArgs : EventArgs
    {
        public string UserId { get; }
        public bool IsSpeaking { get; }

        public SpeakingEventArgs(string userId, bool isSpeaking)
        {
            UserId = userId;
            IsSpeaking = isSpeaking;
        }
    }

    public sealed class SessionStateEventArgs : EventArgs
    {
        public SessionState State { get; }
        public string? Detail { get; }

        public SessionStateEventArgs(SessionState state, string? detail = null)
        {
            State = state;
            Detail = detail;
        }
    }

    /// <summary>
    /// Thrown when Discord rejects a command. Code 4006 (unauthorized scope) is the
    /// expected failure for an app that has not been approved for general RPC access
    /// and is talking to a user outside its tester whitelist.
    /// </summary>
    public sealed class DiscordRpcException : Exception
    {
        public int Code { get; }

        public bool IsScopeDenial => Code == 4006 || Code == 4007;

        public DiscordRpcException(int code, string message) : base(message)
        {
            Code = code;
        }
    }
}
