using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Mirror;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Issue #135: the web menu card's picks ride the intent executor — guarded by the
/// entry text the pilot saw, never a blind ordinal.</summary>
public sealed class GsxMenuControlTests : IDisposable
{
    private static readonly string[] EddlDirectionMenu =
    [
        "Nose Right - Facing East on Taxiway M",
        "Nose Left - Facing West on Taxiway M",
        "Nose Left - Facing North on Taxiway L",
        "QuickEdit Pushback",
        "Straight pushback (manual stop, max 100 m)",
    ];

    private readonly FakeGsxApi _api = new();
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private readonly GsxMenuControl _control;
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "prosimcompanion-tests", Guid.NewGuid().ToString("N"));

    public GsxMenuControlTests()
    {
        var options = new FakeOptionsMonitor(new GsxOptions { MenuOpenTimeoutMs = 300, IntentVerifyTimeoutMs = 300 });
        var executor = new GsxMenuIntentExecutor(_api, options, NullLogger<GsxMenuIntentExecutor>.Instance);
        _control = new GsxMenuControl(
            _api, executor, _diagnostics,
            new JsonlEventLog(_tempDir, NullLogger<JsonlEventLog>.Instance),
            options, NullLogger<GsxMenuControl>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

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
    public async Task Pick_ThirdDirectionLine_SendsThatIndexAndRecordsTheDecision()
    {
        // The line the resolver can never reach (fixed-index fallback covers 0 and 1 only).
        ShowMenu("Select pushback direction", EddlDirectionMenu);
        _api.OnCommand = (verb, args) =>
        {
            if (verb == "menu.pick")
            {
                Assert.Equal(2, (int?)args?["index"]);
                _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        var outcome = await _control.PickAsync(2, EddlDirectionMenu[2]);

        Assert.Equal(GsxMenuActionStatus.Done, outcome.Status);
        Assert.Single(_api.Commands, c => c.Verb == "menu.pick");
        Assert.Contains(_diagnostics.Snapshot().RecentDecisions,
            d => d.Action == "web menu pick" && d.Reason.Contains("Facing North on Taxiway L", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pick_EntryTextMoved_SendsNothing()
    {
        // The pilot clicked line 1 of one menu; by the time the click lands GSX has re-raised
        // a different list. Nothing is sent — the card asks to pick again.
        ShowMenu("Select pushback direction", "Nose Left - Facing West", "Straight pushback");

        var outcome = await _control.PickAsync(1, "Nose Right - Facing East");

        Assert.Equal(GsxMenuActionStatus.NotAvailable, outcome.Status);
        Assert.Empty(_api.Commands);
    }

    [Fact]
    public async Task Pick_NoMenuOpen_IsNotAvailable()
    {
        var outcome = await _control.PickAsync(0, "anything");

        Assert.Equal(GsxMenuActionStatus.NotAvailable, outcome.Status);
        Assert.Empty(_api.Commands);
    }

    [Fact]
    public async Task Pick_ApiNotReady_IsUnavailable()
    {
        _api.Readiness = GsxReadiness.Disconnected;
        ShowMenu("Select pushback direction", EddlDirectionMenu);

        var outcome = await _control.PickAsync(0, EddlDirectionMenu[0]);

        Assert.Equal(GsxMenuActionStatus.Unavailable, outcome.Status);
    }

    [Fact]
    public async Task Pick_GreyedEntry_IsNotAvailable()
    {
        _api.Mirror.ApplyState("menu", new JsonObject
        {
            ["title"] = "Activate Services at Gate 12",
            ["entries"] = new JsonArray(JsonValue.Create("Request Boarding"), JsonValue.Create("Request Pushback")),
            ["disabled"] = new JsonArray(JsonValue.Create(false), JsonValue.Create(true)),
        });
        _api.Mirror.ApplyState("menuShown", JsonValue.Create(true));

        var outcome = await _control.PickAsync(1, "Request Pushback");

        Assert.Equal(GsxMenuActionStatus.NotAvailable, outcome.Status);
        Assert.Contains("greyed", outcome.Detail, StringComparison.Ordinal);
        Assert.Empty(_api.Commands);
    }

    [Fact]
    public async Task Pick_SubmenuOpens_CountsAsTheEffect()
    {
        ShowMenu("Select pushback direction", EddlDirectionMenu);
        _api.OnCommand = (verb, _) =>
        {
            if (verb == "menu.pick")
            {
                ShowMenu("QuickEdit Pushback", "Set stop point", "Back");
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        var outcome = await _control.PickAsync(3, "QuickEdit Pushback");

        Assert.Equal(GsxMenuActionStatus.Done, outcome.Status);
    }

    [Fact]
    public async Task Open_WhenClosed_SendsMenuOpen_WhenShown_SendsNothing()
    {
        _api.OnCommand = (verb, _) =>
        {
            if (verb == "menu.open")
            {
                ShowMenu("Activate Services at Gate 12", "Request Boarding");
            }
            return new GsxCommandResult(true, "ok", null, null);
        };

        Assert.Equal(GsxMenuActionStatus.Done, (await _control.OpenAsync()).Status);
        Assert.Single(_api.Commands, c => c.Verb == "menu.open");

        // A shown menu is never re-opened (menu.open on a shown menu toggles it closed).
        Assert.Equal(GsxMenuActionStatus.Done, (await _control.OpenAsync()).Status);
        Assert.Single(_api.Commands, c => c.Verb == "menu.open");
    }

    [Fact]
    public async Task Close_WhenShown_SendsMenuClose()
    {
        ShowMenu("Select pushback direction", EddlDirectionMenu);

        var outcome = await _control.CloseAsync();

        Assert.Equal(GsxMenuActionStatus.Done, outcome.Status);
        Assert.Single(_api.Commands, c => c.Verb == "menu.close");

        _api.Mirror.ApplyState("menuShown", JsonValue.Create(false));
        Assert.Equal(GsxMenuActionStatus.Done, (await _control.CloseAsync()).Status);
        Assert.Single(_api.Commands, c => c.Verb == "menu.close");
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
