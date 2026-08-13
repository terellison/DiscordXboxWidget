using System;
using System.Security.Cryptography;
using System.Text;

namespace Discord.Rpc
{
    /// <summary>
    /// A PKCE verifier/challenge pair for the authorization code grant.
    /// </summary>
    /// <remarks>
    /// PKCE is what lets this app skip the client secret. Discord requires a secret only
    /// when the token exchange arrives without a code_verifier, so proving possession of
    /// the verifier replaces proving possession of the secret. That matters here because a
    /// secret shipped inside a locally-installed widget is extractable by anyone who has
    /// the package, whereas the verifier is generated fresh per authorization and never
    /// stored.
    ///
    /// Requires the PUBLIC_OAUTH2_CLIENT flag on the Discord application.
    /// </remarks>
    public sealed class PkceChallenge
    {
        /// <summary>The secret half. Sent only on the token exchange, never on AUTHORIZE.</summary>
        public string Verifier { get; }

        /// <summary>The public half. Sent on AUTHORIZE.</summary>
        public string Challenge { get; }

        /// <summary>Discord accepts only S256.</summary>
        public string Method => "S256";

        private PkceChallenge(string verifier, string challenge)
        {
            Verifier = verifier;
            Challenge = challenge;
        }

        public static PkceChallenge Create()
        {
            // 32 bytes base64url-encodes to 43 characters, the minimum Discord accepts.
            var entropy = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(entropy);
            }

            var verifier = Base64Url(entropy);

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
                return new PkceChallenge(verifier, Base64Url(hash));
            }
        }

        /// <summary>
        /// Base64url per RFC 7636: standard base64 with the URL-unsafe characters swapped
        /// and the padding stripped.
        /// </summary>
        private static string Base64Url(byte[] value) =>
            Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
    }
}
