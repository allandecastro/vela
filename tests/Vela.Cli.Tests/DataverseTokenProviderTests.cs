using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class DataverseTokenProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    // A fake clock and a fake credential: each acquire issues "token-N" valid for an hour from the current time.
    private sealed class Fake
    {
        public DateTimeOffset Now = Start;
        public int Acquired;

        public DataverseTokenProvider Provider(DataverseAccessToken initial) =>
            new(initial, _ =>
            {
                int n = Interlocked.Increment(ref Acquired);
                return Task.FromResult(new DataverseAccessToken($"token-{n}", Now.AddHours(1)));
            }, () => Now);
    }

    [Fact]
    public async Task A_valid_token_is_served_from_cache_on_every_request()
    {
        Fake fake = new();
        DataverseTokenProvider provider = fake.Provider(new DataverseAccessToken("initial", Start.AddHours(1)));

        for (int i = 0; i < 5; i++)
        {
            fake.Now = Start.AddMinutes(i * 10);
            Assert.Equal("initial", await provider.GetTokenAsync());
        }

        Assert.Equal(0, fake.Acquired);
    }

    [Fact]
    public async Task An_expired_token_is_renewed_and_the_renewed_one_is_used_afterwards()
    {
        // The cached token from a previous run has five minutes left; the scan and the apply prompt outlive it.
        Fake fake = new();
        DataverseTokenProvider provider = fake.Provider(new DataverseAccessToken("initial", Start.AddMinutes(5)));

        fake.Now = Start.AddMinutes(12);
        Assert.Equal("token-1", await provider.GetTokenAsync());
        Assert.Equal("token-1", await provider.GetTokenAsync());
        Assert.Equal(1, fake.Acquired);

        fake.Now = Start.AddMinutes(12 + 56); // within the refresh margin of token-1's expiry
        Assert.Equal("token-2", await provider.GetTokenAsync());
        Assert.Equal(2, fake.Acquired);
    }

    [Fact]
    public async Task A_token_inside_the_refresh_margin_is_renewed_before_it_lapses()
    {
        Fake fake = new();
        DataverseTokenProvider provider = fake.Provider(new DataverseAccessToken("initial", Start.AddHours(1)));

        fake.Now = Start.AddHours(1) - DataverseTokenProvider.RefreshMargin - TimeSpan.FromSeconds(1);
        Assert.Equal("initial", await provider.GetTokenAsync());

        fake.Now = Start.AddHours(1) - DataverseTokenProvider.RefreshMargin;
        Assert.Equal("token-1", await provider.GetTokenAsync());
    }

    [Fact]
    public async Task Concurrent_requests_on_an_expired_token_renew_it_once()
    {
        TaskCompletionSource release = new();
        int acquired = 0;
        DataverseTokenProvider provider = new(new DataverseAccessToken("initial", Start), async _ =>
        {
            Interlocked.Increment(ref acquired);
            await release.Task;
            return new DataverseAccessToken("renewed", Start.AddHours(1));
        }, () => Start);

        Task<string>[] requests = [.. Enumerable.Range(0, 8).Select(_ => Task.Run(() => provider.GetTokenAsync()))];
        await Task.Delay(50);
        release.SetResult();
        string[] tokens = await Task.WhenAll(requests);

        Assert.All(tokens, t => Assert.Equal("renewed", t));
        Assert.Equal(1, acquired);
    }

    [Fact]
    public async Task The_ServiceClient_callback_asks_the_provider_on_every_call()
    {
        // The callback handed to ServiceClient must not capture a token: each call goes back to the provider.
        Fake fake = new();
        DataverseTokenProvider provider = fake.Provider(new DataverseAccessToken("initial", Start.AddMinutes(30)));
        Func<string, Task<string>> callback = DataverseRegistration.TokenCallback(provider);

        Assert.Equal("initial", await callback("https://org.crm.dynamics.com"));
        fake.Now = Start.AddMinutes(40);
        Assert.Equal("token-1", await callback("https://org.crm.dynamics.com"));
        Assert.Equal(1, fake.Acquired);
    }

    [Fact]
    public async Task A_failed_renewal_surfaces_and_the_next_request_retries()
    {
        int calls = 0;
        DataverseTokenProvider provider = new(new DataverseAccessToken("initial", Start), _ =>
            ++calls == 1 ? throw new InvalidOperationException("credential unavailable") : Task.FromResult(new DataverseAccessToken("renewed", Start.AddHours(1))),
            () => Start);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync());
        Assert.Equal("renewed", await provider.GetTokenAsync());
    }

    [Fact]
    public void The_token_never_appears_in_ToString_or_in_the_options_record()
    {
        DataverseTokenProvider provider = new(new DataverseAccessToken("eyJ-secret-token", Start.AddHours(1)), _ => throw new NotSupportedException());
        SharedOptions options = new([], null, null, "https://org.crm.dynamics.com", provider, null, null, null, false, false, false, false);

        Assert.DoesNotContain("eyJ-secret-token", provider.ToString());
        Assert.DoesNotContain("eyJ-secret-token", new DataverseAccessToken("eyJ-secret-token", Start).ToString());
        Assert.DoesNotContain("eyJ-secret-token", options.ToString());
    }
}
