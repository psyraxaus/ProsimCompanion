using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Mirror;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class GsxMenuIntentExecutorTests
{
    private readonly FakeGsxApi _api = new();
    private readonly Mock<ISimVars> _simVars = new();
    private readonly GsxMenuIntentExecutor _executor;

    public GsxMenuIntentExecutorTests()
        => _executor = new GsxMenuIntentExecutor(
            _api,
            new GsxMenuOpener(_api, _simVars.Object, NullLogger<GsxMenuOpener>.Instance),
            new FakeOptionsMonitor(new GsxOptions { MenuOpenTimeoutMs = 300, IntentVerifyTimeoutMs = 300 }),
            NullLogger<GsxMenuIntentExecutor>.Instance);

    private static GsxMenuIntent Intent(string prefix, string? pattern = null, GsxMenuIntent? parent = null) => new()
    {
        Name = "test",
        TitlePrefixes = [prefix],
        EntryPattern = pattern is null ? null : new Regex(pattern, RegexOptions.IgnoreCase),
        ParentMenu = parent,
    };

    private void ShowMenu(string title, params string[] entries)
    {
        _api.Mirror.ApplyState("menu", new JsonObject
        {
            ["title"] = title,
            ["entries"] = new JsonArray([.. entries.Select(e => JsonValue.Create(e))]),
        });
        _api.Mirror.ApplyState("menuShown", JsonValue.Create(true));
    }

    [Fact]
    public async Task HappyPath_MenuAlreadyShown_PicksAndVerifies()
    {
        ShowMenu("Ice warning: do you request the de-icing treatment?", "Yes please", "No thanks");
        _api.OnCommand = (verb, args) =>
        {
            if (verb == "menu.pick")
            {
                Assert.Equal(0, (int?)args?["index"]);
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false)); // menu dismissed
            }
            return GsxCommandResult.Synthetic("ok") with { Ok = true };
        };

        var result = await _executor.ExecuteAsync(Intent("Ice warning", "^yes"));

        Assert.Equal(GsxIntentOutcome.Success, result.Outcome);
        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.open"); // already shown — no toggle risk
    }

    [Fact]
    public async Task MenuNotShown_OpensAndWaits()
    {
        _api.OnCommand = (verb, _) =>
        {
            if (verb == "menu.open")
            {
                ShowMenu("Activate Services at Gate D57", "Reposition Aircraft", "Operate Jetway");
            }
            if (verb == "menu.pick")
            {
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        var result = await _executor.ExecuteAsync(Intent("Activate Services at", "^operate jetway"));

        Assert.Equal(GsxIntentOutcome.Success, result.Outcome);
        Assert.Contains(_api.Commands, c => c.Verb == "menu.open");
    }

    /// <summary>Issue #141 (2026-09-27 EKCH→EGLL): menu.open answered ok three times in
    /// flight and no menu ever appeared. The legacy menu LVAR is the second rung.</summary>
    [Fact]
    public async Task MenuOpenAcknowledgedButNothingShown_FallsBackToTheMenuLvar()
    {
        _simVars
            .Setup(s => s.WriteAsync(GsxLvarNames.MenuOpen, 1, It.IsAny<CancellationToken>()))
            .Callback(() => ShowMenu("Activate Services at Gate D57", "Reposition Aircraft", "Operate Jetway"))
            .Returns(Task.CompletedTask);
        _api.OnCommand = (verb, _) =>
        {
            if (verb == "menu.pick")
            {
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
            }
            return new GsxCommandResult(true, "ok", null, null); // menu.open: ok, shows nothing
        };

        var result = await _executor.ExecuteAsync(Intent("Activate Services at", "^operate jetway"));

        Assert.Equal(GsxIntentOutcome.Success, result.Outcome);
        _simVars.Verify(s => s.WriteAsync(GsxLvarNames.MenuOpen, 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MenuShownByTheApi_NeverWritesTheMenuLvar()
    {
        _api.OnCommand = (verb, _) =>
        {
            if (verb == "menu.open")
            {
                ShowMenu("Activate Services at Gate D57", "Reposition Aircraft");
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        await _executor.ExecuteAsync(Intent("Activate Services at"));

        _simVars.Verify(s => s.WriteAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoRungShowsAMenu_FailsSafe_MenuDidNotAppear()
    {
        var result = await _executor.ExecuteAsync(Intent("Activate Services at", "^operate jetway"));

        Assert.Equal(GsxIntentOutcome.GsxNoResponse, result.Outcome);
        Assert.StartsWith("menu did not appear", result.Detail);
        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
    }

    [Fact]
    public async Task MsfsNotConnected_TheLvarRungIsSkippedQuietly()
    {
        _simVars
            .Setup(s => s.WriteAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("MSFS is not connected"));

        var result = await _executor.ExecuteAsync(Intent("Activate Services at", "^operate jetway"));

        Assert.Equal(GsxIntentOutcome.GsxNoResponse, result.Outcome);
        Assert.Contains("MSFS not connected", result.Detail);
    }

    [Fact]
    public async Task NotReady_FailsWithoutAnyCommand()
    {
        _api.Readiness = GsxReadiness.ConnectedGsxNotRunning;

        var result = await _executor.ExecuteAsync(Intent("Anything", "^yes"));

        Assert.Equal(GsxIntentOutcome.GsxNoResponse, result.Outcome);
        Assert.Empty(_api.Commands);
    }

    [Fact]
    public async Task NoMatchingEntry_ItemNotAvailable_NoPick()
    {
        ShowMenu("Select de-icing type", "Type I 50%", "Type I 75%");

        var result = await _executor.ExecuteAsync(Intent("Select de-icing type", "Type IV"));

        Assert.Equal(GsxIntentOutcome.ItemNotAvailable, result.Outcome);
        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
    }

    [Fact]
    public async Task MultipleMatches_Ambiguous_NoPick()
    {
        ShowMenu("Select handling operator", "Operator Alpha", "Operator Beta");

        var result = await _executor.ExecuteAsync(Intent("Select handling operator", "Operator"));

        Assert.Equal(GsxIntentOutcome.AmbiguousMatch, result.Outcome);
        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
    }

    [Fact]
    public async Task DisabledEntry_ItemNotAvailable_NoPick()
    {
        _api.Mirror.ApplyState("menu", new JsonObject
        {
            ["title"] = "Activate Services at Gate D57",
            ["entries"] = new JsonArray("Refuel", "Catering"),
            ["disabled"] = new JsonArray(true, false),
        });
        _api.Mirror.ApplyState("menuShown", JsonValue.Create(true));

        var result = await _executor.ExecuteAsync(Intent("Activate Services", "^refuel"));

        Assert.Equal(GsxIntentOutcome.ItemNotAvailable, result.Outcome);
        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
    }

    [Fact]
    public async Task ServerRefusesPickAsDisabled_ItemNotAvailable_NoRetry()
    {
        ShowMenu("Request FollowMe", "Yes", "No");
        _api.OnCommand = (verb, _) => verb == "menu.pick"
            ? new GsxCommandResult(false, "disabled", null, null)
            : new GsxCommandResult(true, "ok", null, null);

        var result = await _executor.ExecuteAsync(Intent("Request FollowMe", "^no$"));

        Assert.Equal(GsxIntentOutcome.ItemNotAvailable, result.Outcome);
        Assert.Equal(1, _api.Commands.Count(c => c.Verb == "menu.pick"));
    }

    [Fact]
    public async Task WrongMenuStaysUp_TitleMismatch()
    {
        ShowMenu("Select Position at Gate D57", "Position 1");
        _api.OnCommand = (_, _) => new GsxCommandResult(true, "ok", null, null); // open acked, menu unchanged

        var result = await _executor.ExecuteAsync(Intent("Activate Services at", "^refuel"));

        Assert.Equal(GsxIntentOutcome.MenuTitleMismatch, result.Outcome);
    }

    [Fact]
    public async Task PickWithoutObservableEffect_GsxNoResponse()
    {
        ShowMenu("Do you want to board crew?", "Yes", "No");
        // Pick acked but the menu never changes — verification must time out.

        var result = await _executor.ExecuteAsync(Intent("Do you want to board crew", "^yes$"));

        Assert.Equal(GsxIntentOutcome.GsxNoResponse, result.Outcome);
    }

    [Fact]
    public async Task PositionalIntent_PicksTheFixedIndex()
    {
        ShowMenu("Select Position at Gate D57", "Position North", "Position South");
        int? picked = null;
        _api.OnCommand = (verb, args) =>
        {
            if (verb == "menu.pick")
            {
                picked = (int?)args?["index"];
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        var result = await _executor.ExecuteAsync(Intent("Select Position at") with { EntryIndex = 1 });

        Assert.Equal(GsxIntentOutcome.Success, result.Outcome);
        Assert.Equal(1, picked);
    }

    [Fact]
    public async Task PositionalIntent_IndexOutOfRange_ItemNotAvailable()
    {
        ShowMenu("Select Position at Gate D57", "Only entry");

        var result = await _executor.ExecuteAsync(Intent("Select Position at") with { EntryIndex = 5 });

        Assert.Equal(GsxIntentOutcome.ItemNotAvailable, result.Outcome);
        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
    }

    [Fact]
    public async Task NavigationOnlyIntent_SucceedsOnTitleMatch()
    {
        ShowMenu("Activate Services at Gate D57", "Refuel");

        var result = await _executor.ExecuteAsync(Intent("Activate Services at"));

        Assert.Equal(GsxIntentOutcome.Success, result.Outcome);
        Assert.Empty(_api.Commands);
    }

    [Fact]
    public async Task ParentNavigation_PicksParentThenChild()
    {
        ShowMenu("Activate Services at Gate D57", "Reposition Aircraft", "Refuel");
        var parent = Intent("Activate Services at", "^reposition aircraft") with
        {
            Verify = mirror => mirror.Menu?.Title.StartsWith("Select Position", StringComparison.OrdinalIgnoreCase) == true,
        };
        _api.OnCommand = (verb, args) =>
        {
            if (verb == "menu.pick" && (int?)args?["index"] == 0
                && _api.Mirror.Menu?.Title.StartsWith("Activate", StringComparison.Ordinal) == true)
            {
                ShowMenu("Select Position at Gate D57", "Position 1", "Position 2");
            }
            else if (verb == "menu.pick")
            {
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        var result = await _executor.ExecuteAsync(Intent("Select Position at", "^position 2$", parent));

        Assert.Equal(GsxIntentOutcome.Success, result.Outcome);
        Assert.Equal(2, _api.Commands.Count(c => c.Verb == "menu.pick"));
    }

    /// <summary>Issue #157 (EFHK→LKPR 2026-10-04): in flight GSX's root menu is itself the
    /// "Select airport" page. The parent's "^select airport" entry does not exist there — the
    /// child's row pick must run on the opened page, with no parent pick.</summary>
    [Fact]
    public async Task ParentOpen_LandsOnTheChildPage_RunsTheChildDirectly()
    {
        var root = Intent("", "^select airport");
        var airportPick = Intent("Select airport", @"\bLKPR\b", root) with
        {
            Verify = mirror => !mirror.MenuShown,
        };
        var picks = new List<int?>();
        _api.OnCommand = (verb, args) =>
        {
            if (verb == "menu.open")
            {
                ShowMenu("Select airport", "EFHK Vantaa at 301.20 nm", "LKPR Ruzyne at 592.70 nm [PLANNED]", "EETN Tallinn at 12.40 nm");
            }
            else if (verb == "menu.pick")
            {
                picks.Add((int?)args?["index"]);
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        var result = await _executor.ExecuteAsync(airportPick);

        Assert.Equal(GsxIntentOutcome.Success, result.Outcome);
        Assert.Equal([1], picks); // the LKPR row only — no root pick
    }

    /// <summary>2026-10-10 EGCC attempt 1 (ticket t-20261010-0726): after our menu.open the
    /// mirror showed the menu with an EMPTY title for a beat while the rows were already the
    /// airport list; the parent resolved "^select airport" against that beat and failed
    /// ItemNotAvailable "on ''". The title must be waited for before the page is judged.</summary>
    [Fact]
    public async Task ParentOpen_TitleLagsMenuShown_WaitsAndRunsTheChild()
    {
        var root = Intent("", "^select airport");
        var airportPick = Intent("Select airport", @"\bEGCC\b", root) with
        {
            Verify = mirror => !mirror.MenuShown,
        };
        var picks = new List<int?>();
        _api.OnCommand = (verb, args) =>
        {
            if (verb == "menu.open")
            {
                _api.Mirror.ApplyState("menu", new JsonObject
                {
                    ["title"] = "",
                    ["entries"] = new JsonArray("Moving Map", "EGCC Manchester at 878.43 nm [PLANNED]", "EFQI7 at 15.26 nm"),
                });
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(true));
                _ = Task.Run(async () =>
                {
                    await Task.Delay(120);
                    ShowMenu("Select airport", "Moving Map", "EGCC Manchester at 878.43 nm [PLANNED]", "EFQI7 at 15.26 nm");
                });
            }
            else if (verb == "menu.pick")
            {
                picks.Add((int?)args?["index"]);
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        var result = await _executor.ExecuteAsync(airportPick);

        Assert.Equal(GsxIntentOutcome.Success, result.Outcome);
        Assert.Equal([1], picks); // the EGCC row only — nothing picked on the untitled beat
    }

    /// <summary>The root page that DOES carry the entry keeps the two-step path.</summary>
    [Fact]
    public async Task ParentOpen_LandsOnARootMenu_StillPicksTheParentEntry()
    {
        var root = Intent("", "^select airport") with
        {
            Verify = mirror => mirror.MenuShown
                && mirror.Menu?.Title.StartsWith("Select airport", StringComparison.OrdinalIgnoreCase) == true,
        };
        var airportPick = Intent("Select airport", @"\bLKPR\b", root) with
        {
            Verify = mirror => !mirror.MenuShown,
        };
        var picks = new List<int?>();
        _api.OnCommand = (verb, args) =>
        {
            if (verb == "menu.open")
            {
                ShowMenu("GSX", "Select airport", "Settings");
            }
            else if (verb == "menu.pick" && _api.Mirror.Menu?.Title == "GSX")
            {
                picks.Add((int?)args?["index"]);
                ShowMenu("Select airport", "LKPR Ruzyne at 592.70 nm [PLANNED]");
            }
            else if (verb == "menu.pick")
            {
                picks.Add((int?)args?["index"]);
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        var result = await _executor.ExecuteAsync(airportPick);

        Assert.Equal(GsxIntentOutcome.Success, result.Outcome);
        Assert.Equal([0, 0], picks);
    }

    /// <summary>A stale "Select airport" title left from an earlier open must not let the
    /// child pick on a different menu: the title has to hold through the settle.</summary>
    [Fact]
    public async Task ParentOpen_StaleChildTitle_IsNotTrusted()
    {
        var root = Intent("", "^select airport");
        var airportPick = Intent("Select airport", @"\bLKPR\b", root);
        _api.Mirror.ApplyState("menu", new JsonObject
        {
            ["title"] = "Select airport",
            ["entries"] = new JsonArray("LKPR Ruzyne at 592.70 nm [PLANNED]"),
        });
        Task? freshMenu = null;
        _api.OnCommand = (verb, args) =>
        {
            if (verb == "menu.open")
            {
                // menuShown first; the fresh menu arrives a beat later.
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(true));
                freshMenu = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    ShowMenu("Change parking or service", "Change Facility [Gate C 29]", "Request Deboarding");
                });
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        var result = await _executor.ExecuteAsync(airportPick);

        Assert.NotNull(freshMenu);
        await freshMenu;
        Assert.Equal(GsxIntentOutcome.ItemNotAvailable, result.Outcome);
        Assert.DoesNotContain(_api.Commands, c => c.Verb == "menu.pick");
    }

    /// <summary>2026-09-13/20 flights: the reposition step opens GSX's "Select Position at …"
    /// list and the question catalogue took it for the unknown-parking prompt. The executor
    /// must own up to the menus it drives — the child, its parent, and for a grace period
    /// after the pick.</summary>
    [Fact]
    public async Task IsDriving_CoversTheIntentChainWhileRunning_AndBrieflyAfter()
    {
        var gateMenu = Intent("Activate Services at", "^reposition aircraft") with
        {
            Verify = mirror => mirror.MenuShown
                && mirror.Menu?.Title.StartsWith("Select Position", StringComparison.OrdinalIgnoreCase) == true,
        };
        var positionPick = new GsxMenuIntent
        {
            Name = "reposition position pick",
            TitlePrefixes = ["Select Position at"],
            EntryIndex = 0,
            ParentMenu = gateMenu,
        };
        Assert.False(_executor.IsDriving("Select Position at LIRF/Fiumicino"));

        var drivingDuringSubmenu = false;
        _api.OnCommand = (verb, args) =>
        {
            if (verb == "menu.open")
            {
                ShowMenu("Activate Services at LIRF/Fiumicino", "Reposition Aircraft");
            }
            else if (verb == "menu.pick" && (int?)args?["index"] == 0
                && _api.Mirror.Menu?.Title.StartsWith("Activate", StringComparison.Ordinal) == true)
            {
                ShowMenu("Select Position at LIRF/Fiumicino", "Reposition here [Remote Stands 8XX/9XX | Stand 835]");
                drivingDuringSubmenu = _executor.IsDriving("Select Position at LIRF/Fiumicino");
            }
            else if (verb == "menu.pick")
            {
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        var result = await _executor.ExecuteAsync(positionPick);

        Assert.Equal(GsxIntentOutcome.Success, result.Outcome);
        Assert.True(drivingDuringSubmenu);
        // Grace after the pick: the dispatcher may reach its handler a beat later.
        Assert.True(_executor.IsDriving("Select Position at LIRF/Fiumicino"));
        Assert.True(_executor.IsDriving("Activate Services at LIRF/Fiumicino"));
        Assert.False(_executor.IsDriving("Request FollowMe?"));
        Assert.False(_executor.IsDriving(null));
    }

    private sealed class FakeGsxApi : IGsxRemoteApi
    {
#pragma warning disable CS0067 // raised by the real client; not needed by these scenarios
        public event Action<GsxReadiness>? ReadinessChanged;
#pragma warning restore CS0067

        public GsxReadiness Readiness { get; set; } = GsxReadiness.Ready;
        public GsxStateMirror Mirror { get; } = new();
        public List<(string Verb, JsonObject? Args)> Commands { get; } = [];
        public Func<string, JsonObject?, GsxCommandResult>? OnCommand { get; set; }

        public bool HasCapability(string token) => true;

        public Task<GsxCommandResult> SendCommandAsync(string verb, JsonObject? args, CancellationToken cancellationToken = default)
        {
            Commands.Add((verb, args));
            return Task.FromResult(OnCommand?.Invoke(verb, args) ?? new GsxCommandResult(true, "ok", null, null));
        }
    }

    private sealed class FakeOptionsMonitor(GsxOptions value) : IOptionsMonitor<GsxOptions>
    {
        public GsxOptions CurrentValue { get; } = value;
        public GsxOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GsxOptions, string?> listener) => null;
    }
}
