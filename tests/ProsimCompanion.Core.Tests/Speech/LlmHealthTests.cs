using System.Net;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// Issue #66: the LLM health state machine. A 401 once ran a whole flight invisibly — every
/// call outcome must now land in <see cref="LlmHealthStore"/> so the web banner and the FO's
/// one-shot advisory have something to read.
/// </summary>
public sealed class LlmHealthTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    private static BriefingOptions Options() => new()
    {
        LlmEnabled = true,
        LlmBaseUrl = "http://llm.test/v1",
        LlmModel = "test-model",
        LlmApiKey = "sk-SECRET-NEVER-LOGGED",
    };

    private static OpenAiChatClient Client(HttpMessageHandler handler, LlmHealthStore health)
        => new(SpeechTestSupport.BriefingMonitor(Options()), new HttpClient(handler), health);

    [Fact]
    public async Task Http401_MarksAuthFailed_AndSummaryNeverCarriesTheKey()
    {
        var health = new LlmHealthStore();
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), health);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("s", "u"));

        var snapshot = health.Snapshot();
        Assert.Equal(LlmHealthState.AuthFailed, snapshot.State);
        Assert.True(snapshot.IsUnhealthy);
        Assert.DoesNotContain("SECRET", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Http403_AlsoMarksAuthFailed()
    {
        var health = new LlmHealthStore();
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)), health);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(LlmHealthState.AuthFailed, health.Snapshot().State);
    }

    [Fact]
    public async Task Http500_MarksUnreachable_NotAuthFailed()
    {
        var health = new LlmHealthStore();
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)), health);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(LlmHealthState.Unreachable, health.Snapshot().State);
    }

    [Fact]
    public async Task TransportError_MarksUnreachable()
    {
        var health = new LlmHealthStore();
        var client = Client(new ThrowingHandler(), health);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("s", "u"));

        Assert.Equal(LlmHealthState.Unreachable, health.Snapshot().State);
    }

    [Fact]
    public async Task Success_MarksHealthy_RecoveringFromAuthFailed()
    {
        var health = new LlmHealthStore();
        health.Report(LlmHealthState.AuthFailed, "HTTP 401");
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"content":"OK"}}]}"""),
        }), health);

        var reply = await client.CompleteAsync("s", "u");

        Assert.Equal("OK", reply);
        Assert.Equal(LlmHealthState.Healthy, health.Snapshot().State);
        Assert.False(health.Snapshot().IsUnhealthy);
    }

    [Fact]
    public async Task CallerCancel_NeverPoisonsHealth()
    {
        var health = new LlmHealthStore();
        var client = Client(new ThrowingHandler(), health);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CompleteAsync("s", "u", cts.Token));

        Assert.Equal(LlmHealthState.Unknown, health.Snapshot().State);
    }

    [Fact]
    public void Store_FiresChangedOnTransitionsOnly()
    {
        var store = new LlmHealthStore();
        var fired = 0;
        store.Changed += (_, _) => fired++;

        store.Report(LlmHealthState.Healthy);
        store.Report(LlmHealthState.Healthy); // same state + summary — no churn
        store.Report(LlmHealthState.AuthFailed, "HTTP 401 from the chat-completions endpoint");

        Assert.Equal(2, fired);
    }

    // ---- The probe decision (pure) ----

    [Theory]
    [InlineData(true, LlmHealthState.Unknown, true)]      // startup seed
    [InlineData(true, LlmHealthState.AuthFailed, true)]   // recovery watch
    [InlineData(true, LlmHealthState.Unreachable, true)]  // recovery watch
    [InlineData(true, LlmHealthState.Healthy, false)]     // live calls keep the store current
    [InlineData(false, LlmHealthState.Unknown, false)]    // unconfigured never probes
    [InlineData(false, LlmHealthState.AuthFailed, false)]
    public void ShouldProbe_OnlyWhileConfiguredAndNotHealthy(
        bool configured, LlmHealthState state, bool expected)
        => Assert.Equal(expected, LlmHealthProbeService.ShouldProbe(configured, state));

    // ---- The idle-miss response (pure; issue #66's spoken half) ----

    [Fact]
    public void IdleMiss_FirstMissWhileLlmDown_SpeaksTheAdvisoryOnce()
    {
        var first = IdleMissPolicy.Decide(LlmHealthState.AuthFailed, advisoryAlreadyGiven: false, "Say again?");

        Assert.Equal(IdleMissPolicy.LlmOfflineAdvisory, first.Text);
        Assert.Equal("advisory", first.Tag);
        Assert.True(first.IsLlmOfflineAdvisory);
    }

    [Fact]
    public void IdleMiss_AfterTheAdvisory_FallsBackToDidNotCatch()
    {
        var next = IdleMissPolicy.Decide(LlmHealthState.AuthFailed, advisoryAlreadyGiven: true, "Say again?");

        Assert.Equal("Say again?", next.Text);
        Assert.Equal("reject", next.Tag);
        Assert.False(next.IsLlmOfflineAdvisory);
    }

    [Theory]
    [InlineData(LlmHealthState.Unknown)]
    [InlineData(LlmHealthState.Healthy)]
    public void IdleMiss_LlmFineOrUntested_IsAPlainDidNotCatch(LlmHealthState state)
    {
        var response = IdleMissPolicy.Decide(state, advisoryAlreadyGiven: false, "Didn't catch that.");

        Assert.Equal("Didn't catch that.", response.Text);
        Assert.Equal("reject", response.Tag);
    }
}
