namespace Vela.Cli;

/// <summary>
/// Supplies the bearer token for the interactive (MSAL) and ambient (Azure.Identity) connection paths. The
/// <c>ServiceClient</c> calls <see cref="GetTokenAsync"/> before its requests, so a run that outlives the first token
/// (a long scan, a human at the apply prompt, a long apply) gets a fresh one instead of failing with 401 mid-apply.
/// <para>
/// The first token is acquired up front, when the connection is resolved, so an authentication failure still surfaces
/// before anything is scanned or written. A cached token is returned until it is within <see cref="RefreshMargin"/>
/// of its expiry; then the acquire callback runs again (MSAL <c>AcquireTokenSilent</c> or the credential's
/// <c>GetToken</c>, which renew through their own caches). The connection-string path does not use this type: the
/// SDK's <c>ServiceClient</c> renews its own token there.
/// </para>
/// <para>
/// The token is never exposed by <see cref="object.ToString"/>, so a <see cref="SharedOptions"/> printed or logged
/// does not carry it.
/// </para>
/// </summary>
public sealed class DataverseTokenProvider
{
    /// <summary>A token this close to its expiry is renewed before use, so it cannot lapse mid-request.</summary>
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<DataverseAccessToken>> _acquire;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile DataverseAccessToken _current; // a reference, swapped whole, so a lock-free read is never torn

    internal DataverseTokenProvider(DataverseAccessToken initial, Func<CancellationToken, Task<DataverseAccessToken>> acquire, Func<DateTimeOffset>? now = null)
    {
        _current = initial;
        _acquire = acquire;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Returns a token valid for at least <see cref="RefreshMargin"/>, renewing the cached one if needed.</summary>
    internal async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        DataverseAccessToken snapshot = _current;
        if (!NeedsRefresh(snapshot))
        {
            return snapshot.Token;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have renewed it while this one waited.
            if (NeedsRefresh(_current))
            {
                _current = await _acquire(cancellationToken).ConfigureAwait(false);
            }

            return _current.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override string ToString() => nameof(DataverseTokenProvider);

    private bool NeedsRefresh(DataverseAccessToken token) => _now() >= token.ExpiresOn - RefreshMargin;
}

/// <summary>A bearer token and its expiry. <see cref="ToString"/> never prints the token.</summary>
public sealed record DataverseAccessToken(string Token, DateTimeOffset ExpiresOn)
{
    public override string ToString() => $"DataverseAccessToken(ExpiresOn={ExpiresOn:O})";
}
