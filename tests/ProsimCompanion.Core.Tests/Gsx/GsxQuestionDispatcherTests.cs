using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Gsx.Menu;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class GsxQuestionDispatcherTests : IDisposable
{
    private readonly GsxQuestionDispatcher _dispatcher = new(NullLogger<GsxQuestionDispatcher>.Instance);

    public void Dispose() => _dispatcher.Dispose();

    [Fact]
    public async Task RisingEdge_DispatchesOncePerTitleAppearance()
    {
        var calls = 0;
        _dispatcher.Register("Ice warning", _ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await _dispatcher.OnMenuUpdatedAsync(true, "Ice warning: do you request the de-icing treatment?");
        await _dispatcher.OnMenuUpdatedAsync(true, "Ice warning: do you request the de-icing treatment?");

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReRaiseAfterHide_DispatchesAgain()
    {
        var calls = 0;
        _dispatcher.Register("Do you want to board crew", _ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await _dispatcher.OnMenuUpdatedAsync(true, "Do you want to board crew?");
        await _dispatcher.OnMenuUpdatedAsync(false, null);
        await _dispatcher.OnMenuUpdatedAsync(true, "Do you want to board crew?");

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TitleChangeWhileShown_DispatchesNewHandler()
    {
        var log = new List<string>();
        _dispatcher.Register("Request FollowMe", _ => { log.Add("followme"); return Task.CompletedTask; });
        _dispatcher.Register("Select handling operator", _ => { log.Add("operator"); return Task.CompletedTask; });

        await _dispatcher.OnMenuUpdatedAsync(true, "Request FollowMe car?");
        await _dispatcher.OnMenuUpdatedAsync(true, "Select handling operator");

        Assert.Equal(["followme", "operator"], log);
    }

    [Fact]
    public async Task UnknownTitle_NothingDispatched_HandlerFailureContained()
    {
        _dispatcher.Register("Known", _ => throw new InvalidOperationException("boom"));

        await _dispatcher.OnMenuUpdatedAsync(true, "Completely different menu");
        await _dispatcher.OnMenuUpdatedAsync(false, null);
        await _dispatcher.OnMenuUpdatedAsync(true, "Known question?");   // throws inside — must not propagate
    }

    [Fact]
    public async Task FirstRegisteredMatchWins()
    {
        var log = new List<string>();
        _dispatcher.Register("Interrupt pushback", _ => { log.Add("first"); return Task.CompletedTask; });
        _dispatcher.Register("Interrupt", _ => { log.Add("second"); return Task.CompletedTask; });

        await _dispatcher.OnMenuUpdatedAsync(true, "Interrupt pushback?");

        Assert.Equal(["first"], log);
    }
}
