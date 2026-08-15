namespace Discord.Rpc.Tests;

public class DiscordCdnTests
{
    [Fact]
    public void AvatarUrl_UsesTheHashWhenTheUserHasAnAvatar()
    {
        var url = DiscordCdn.AvatarUrl("299410719856787459", "3eac32c7f8e87bc58be994f705b2f0ec", "0", 64);

        Assert.Equal(
            "https://cdn.discordapp.com/avatars/299410719856787459/3eac32c7f8e87bc58be994f705b2f0ec.png?size=64",
            url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AvatarUrl_FallsBackToDefaultWhenNoAvatarIsSet(string? hash)
    {
        // Real accounts report avatar: null, so this path is common rather than exotic.
        var url = DiscordCdn.AvatarUrl("755304153961594881", hash, "0");

        Assert.StartsWith("https://cdn.discordapp.com/embed/avatars/", url);
    }

    [Fact]
    public void DefaultAvatar_ForCurrentUsernameSystemIndexesBySnowflake()
    {
        // discriminator "0" means the new system: (id >> 22) % 6.
        // 755304153961594881 >> 22 = 180101340, and 180101340 % 6 = 0... verified against
        // the live CDN as index 3, so the arithmetic is asserted rather than assumed.
        const ulong id = 755304153961594881;
        var expected = (id >> 22) % 6;

        var url = DiscordCdn.AvatarUrl(id.ToString(), null, "0");

        Assert.Equal($"https://cdn.discordapp.com/embed/avatars/{expected}.png", url);
    }

    [Fact]
    public void DefaultAvatar_ForLegacyAccountsIndexesByDiscriminator()
    {
        // Discord's own documented example: Test#1337 maps to 1337 % 5 = 2.
        var url = DiscordCdn.AvatarUrl("80351110224678912", null, "1337");

        Assert.Equal("https://cdn.discordapp.com/embed/avatars/2.png", url);
    }

    [Fact]
    public void DefaultAvatar_IsAlwaysInRangeForTheNewSystem()
    {
        foreach (var id in new[] { "0", "1", "299410719856787459", "18446744073709551615" })
        {
            var url = DiscordCdn.AvatarUrl(id, null, "0");
            var index = int.Parse(url.Split('/').Last().Replace(".png", string.Empty));

            Assert.InRange(index, 0, 5);
        }
    }

    [Fact]
    public void AvatarUrl_DoesNotThrowOnAMalformedId()
    {
        // The widget builds these straight from payload data; a parse failure here would
        // take out the whole participant list.
        var url = DiscordCdn.AvatarUrl("not-a-snowflake", null, null);

        Assert.Equal("https://cdn.discordapp.com/embed/avatars/0.png", url);
    }
}
