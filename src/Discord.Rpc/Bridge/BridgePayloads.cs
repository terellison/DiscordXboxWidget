using System.Collections.Generic;
using System.Text.Json;

namespace Discord.Rpc.Bridge
{
    /// <summary>
    /// Hand-rolled JSON for the bridge payloads.
    /// </summary>
    /// <remarks>
    /// Explicit reads and writes rather than reflection-based serialization: the widget is
    /// compiled with .NET Native in Release, where reflective serialization of these types
    /// is exactly the thing that gets trimmed away and fails at runtime instead of at build.
    /// </remarks>
    public static class BridgePayloads
    {
        public static string WriteChannel(VoiceChannelSnapshot? channel)
        {
            if (channel == null) return "null";

            using var stream = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("id", channel.Id);
                w.WriteString("name", channel.Name);
                if (channel.GuildId != null) w.WriteString("guildId", channel.GuildId);

                w.WriteStartArray("participants");
                foreach (var p in channel.Participants)
                {
                    w.WriteStartObject();
                    w.WriteString("id", p.Id);
                    w.WriteString("username", p.Username);
                    if (p.Nickname != null) w.WriteString("nickname", p.Nickname);
                    w.WriteBoolean("muted", p.IsMuted);
                    w.WriteBoolean("deafened", p.IsDeafened);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        public static VoiceChannelSnapshot? ReadChannel(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null") return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var participants = new List<VoiceUser>();
            if (root.TryGetProperty("participants", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in arr.EnumerateArray())
                {
                    participants.Add(new VoiceUser(
                        id: Str(p, "id") ?? string.Empty,
                        username: Str(p, "username") ?? "unknown",
                        nickname: Str(p, "nickname"),
                        isMuted: Bool(p, "muted"),
                        isDeafened: Bool(p, "deafened")));
                }
            }

            return new VoiceChannelSnapshot(
                Str(root, "id") ?? string.Empty,
                Str(root, "name") ?? "Voice",
                Str(root, "guildId"),
                participants);
        }

        public static string WriteVoiceSettings(LocalVoiceSettings settings) =>
            $"{{\"muted\":{(settings.IsMuted ? "true" : "false")},\"deafened\":{(settings.IsDeafened ? "true" : "false")}}}";

        public static LocalVoiceSettings ReadVoiceSettings(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return new LocalVoiceSettings(Bool(doc.RootElement, "muted"), Bool(doc.RootElement, "deafened"));
        }

        public static string WriteSpeaking(string userId, bool speaking) =>
            $"{{\"userId\":{JsonSerializer.Serialize(userId)},\"speaking\":{(speaking ? "true" : "false")}}}";

        public static SpeakingEventArgs ReadSpeaking(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return new SpeakingEventArgs(Str(doc.RootElement, "userId") ?? string.Empty, Bool(doc.RootElement, "speaking"));
        }

        public static string WriteState(SessionState state, string? detail, SessionCapabilities capabilities, string? currentUserId)
        {
            using var stream = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("state", state.ToString());
                if (detail != null) w.WriteString("detail", detail);
                w.WriteNumber(BridgeProtocol.FieldCapabilities, (int)capabilities);
                if (currentUserId != null) w.WriteString(BridgeProtocol.FieldCurrentUserId, currentUserId);
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        public static void ReadState(
            string json, out SessionState state, out string? detail,
            out SessionCapabilities capabilities, out string? currentUserId)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            state = SessionState.Disconnected;
            var name = Str(root, "state");
            if (name != null)
            {
#if NETSTANDARD2_0
                try { state = (SessionState)System.Enum.Parse(typeof(SessionState), name); } catch { }
#else
                System.Enum.TryParse(name, out state);
#endif
            }

            detail = Str(root, "detail");
            capabilities = root.TryGetProperty(BridgeProtocol.FieldCapabilities, out var c) && c.TryGetInt32(out var ci)
                ? (SessionCapabilities)ci
                : SessionCapabilities.None;
            currentUserId = Str(root, BridgeProtocol.FieldCurrentUserId);
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
