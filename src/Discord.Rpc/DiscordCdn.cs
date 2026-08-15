using System;
using System.Globalization;

namespace Discord.Rpc
{
    /// <summary>
    /// Builds Discord CDN URLs from the identifiers the RPC payloads carry.
    /// </summary>
    public static class DiscordCdn
    {
        private const string Root = "https://cdn.discordapp.com";

        /// <summary>
        /// Avatar URL for a user, falling back to their default avatar when they have not
        /// set one (the RPC payload reports avatar: null for those users).
        /// </summary>
        /// <param name="userId">Snowflake id.</param>
        /// <param name="avatarHash">The user.avatar field, or null.</param>
        /// <param name="discriminator">
        /// The user.discriminator field. "0" means the account is on the current username
        /// system, which picks its default avatar differently from legacy accounts.
        /// </param>
        /// <param name="size">Requested pixel size; Discord accepts powers of two.</param>
        public static string AvatarUrl(string userId, string? avatarHash, string? discriminator, int size = 64)
        {
            if (!string.IsNullOrEmpty(avatarHash))
            {
                // Animated avatars use an a_ prefixed hash. Requesting .png still returns a
                // static frame, which is what a small list row wants anyway.
                return $"{Root}/avatars/{userId}/{avatarHash}.png?size={size}";
            }

            return $"{Root}/embed/avatars/{DefaultAvatarIndex(userId, discriminator)}.png";
        }

        private static int DefaultAvatarIndex(string userId, string? discriminator)
        {
            // Legacy accounts still carry a real discriminator and index modulo 5.
            if (!string.IsNullOrEmpty(discriminator)
                && discriminator != "0"
                && int.TryParse(discriminator, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacy))
            {
                return legacy % 5;
            }

            // Current username system: derived from the snowflake's timestamp bits, modulo 6.
            if (ulong.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var snowflake))
                return (int)((snowflake >> 22) % 6);

            return 0;
        }
    }
}
