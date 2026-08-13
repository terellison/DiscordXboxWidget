using System.Threading;
using System.Threading.Tasks;

namespace Discord.Rpc
{
    /// <summary>
    /// Exchanges an RPC authorization code for an access token.
    /// </summary>
    /// <remarks>
    /// This is deliberately not implemented inside the library. The exchange requires the
    /// application's client secret, and a secret shipped inside a locally-installed widget
    /// is not actually secret — anyone can extract it from the package. That is an accepted
    /// wart for a personal/whitelisted build, but it is a real blocker for public
    /// distribution, so the decision is pushed to the host application rather than baked in.
    ///
    /// A host should cache the resulting token and refresh it rather than re-running the
    /// authorize prompt on every widget launch; Discord shows a user-facing consent dialog
    /// for each AUTHORIZE call.
    /// </remarks>
    public interface IOAuthTokenProvider
    {
        /// <summary>Returns a cached access token, or null if the authorize flow must run.</summary>
        Task<string?> TryGetCachedTokenAsync(CancellationToken cancellationToken);

        /// <summary>Exchanges an authorization code from the AUTHORIZE command for an access token.</summary>
        Task<string> ExchangeCodeAsync(string authorizationCode, CancellationToken cancellationToken);
    }
}
