using System.Security.Cryptography;
using System.Text;

namespace Discord.Rpc.Tests;

public class PkceChallengeTests
{
    [Fact]
    public void Verifier_MeetsRfc7636LengthAndCharset()
    {
        var pkce = PkceChallenge.Create();

        Assert.InRange(pkce.Verifier.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9._~-]+$", pkce.Verifier);
    }

    [Fact]
    public void Challenge_IsBase64UrlSha256OfVerifier()
    {
        var pkce = PkceChallenge.Create();

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(pkce.Verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        Assert.Equal(expected, pkce.Challenge);
    }

    [Fact]
    public void Challenge_IsUrlSafeAndUnpadded()
    {
        // Padding or + and / would be mangled in transit and Discord would reject the
        // exchange with a mismatch that looks like a server-side problem.
        var pkce = PkceChallenge.Create();

        Assert.DoesNotContain('=', pkce.Challenge);
        Assert.DoesNotContain('+', pkce.Challenge);
        Assert.DoesNotContain('/', pkce.Challenge);
    }

    [Fact]
    public void Create_ProducesADistinctVerifierEachTime()
    {
        // Reusing a verifier across authorizations would defeat the point of PKCE.
        var verifiers = Enumerable.Range(0, 50).Select(_ => PkceChallenge.Create().Verifier).ToList();

        Assert.Equal(verifiers.Count, verifiers.Distinct().Count());
    }

    [Fact]
    public void Method_IsS256()
    {
        Assert.Equal("S256", PkceChallenge.Create().Method);
    }
}
