using System.Threading;
using System.Threading.Tasks;

namespace Discord.Rpc
{
    /// <summary>
    /// Exchanges an RPC authorization code for an access token, and caches the result.
    /// </summary>
    /// <remarks>
    /// The exchange uses PKCE rather than a client secret, so implementations do not need
    /// to store one. See <see cref="PkceChallenge"/> for why that matters for a
    /// locally-installed app.
    ///
    /// Implementations should cache the resulting token and return it from
    /// <see cref="TryGetCachedTokenAsync"/>; Discord shows a user-facing consent dialog on
    /// every AUTHORIZE call, so re-running the flow on each launch is user-visible.
    /// </remarks>
    public interface IOAuthTokenProvider
    {
        /// <summary>Returns a cached access token, or null if the authorize flow must run.</summary>
        Task<string?> TryGetCachedTokenAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Exchanges an authorization code for an access token and caches it.
        /// </summary>
        /// <param name="codeVerifier">
        /// The PKCE verifier matching the challenge sent on AUTHORIZE. Must be forwarded as
        /// code_verifier, or Discord will demand a client secret instead.
        /// </param>
        Task<string> ExchangeCodeAsync(string authorizationCode, string codeVerifier, CancellationToken cancellationToken);
    }
}
