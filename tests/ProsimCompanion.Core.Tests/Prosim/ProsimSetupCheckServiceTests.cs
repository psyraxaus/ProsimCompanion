using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Aircraft.Setup;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Prosim.Setup;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

/// <summary>
/// The ProSim setup check service (2026-10-06) over a scripted gateway: what it reads, when,
/// what it writes (only on Apply, only mismatches, never ignored rows) and how it reports.
/// </summary>
public sealed class ProsimSetupCheckServiceTests : IDisposable
{
    private readonly string _sessionsDir = Path.Combine(Path.GetTempPath(), "pc-setup-check-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IProsimGateway> _gateway = new(MockBehavior.Strict);
    private readonly Dictionary<string, string?> _prosim = new(StringComparer.Ordinal)
    {
        [ProsimSetupRecommendations.ProsimVersion] = "1.75.1",
        [ProsimSetupRecommendations.DoorLogic] = "false",
        [ProsimSetupRecommendations.AutomaticGroundPower] = "false",
        [ProsimSetupRecommendations.DatalinkLoadCargo] = "false",
        [ProsimSetupRecommendations.DatalinkLoadFuel] = "false",
        [ProsimSetupRecommendations.RefuelRate] = "Realistic",
    };
    private readonly List<(string Name, object Value)> _writes = [];
    private readonly ConnectionStatusStore _status = new();
    private readonly ProsimSetupStore _store = new();
    private readonly ProsimOptions _prosimOptions = new();
    private readonly GsxOptions _gsxOptions = new();
    private readonly JsonlEventLog _eventLog;
    private bool _rejectWrites;
    /// <summary>Reads that still show the old value after a write (ProSim applies a beat after it
    /// acknowledges; sim PC 2026-10-11).</summary>
    private int _staleReadsAfterWrite;
    private readonly Dictionary<string, string?> _applied = new(StringComparer.Ordinal);
    private int _delays;

    public ProsimSetupCheckServiceTests()
    {
        Directory.CreateDirectory(_sessionsDir);
        _eventLog = new JsonlEventLog(_sessionsDir, NullLogger<JsonlEventLog>.Instance);

        _gateway.Setup(g => g.IsReachableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _gateway.Setup(g => g.QueryDataRefAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) =>
            {
                // The version query starts every read-back pass: count passes there and let the
                // pending writes land once the stale passes are used up.
                if (name == ProsimSetupRecommendations.ProsimVersion && _applied.Count > 0 && _staleReadsAfterWrite-- <= 0)
                {
                    foreach (var pair in _applied) { _prosim[pair.Key] = pair.Value; }
                    _applied.Clear();
                }

                return _prosim.GetValueOrDefault(name);
            });
        _gateway.Setup(g => g.WriteDataRefAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, object value, CancellationToken _) =>
            {
                _writes.Add((name, value));
                if (_rejectWrites)
                {
                    return false;
                }

                var text = value is bool b ? (b ? "true" : "false") : value.ToString();
                if (_staleReadsAfterWrite > 0)
                {
                    _applied[name] = text;
                }
                else
                {
                    _prosim[name] = text;
                }

                return true;
            });
    }

    public void Dispose()
    {
        _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_sessionsDir, recursive: true); } catch (IOException) { }
    }

    private ProsimSetupCheckService Create() => new(
        _gateway.Object,
        _status,
        _store,
        new FakeMonitor<ProsimOptions>(_prosimOptions),
        new FakeMonitor<GsxOptions>(_gsxOptions),
        _eventLog,
        NullLogger<ProsimSetupCheckService>.Instance,
        (_, _) => { _delays++; return Task.CompletedTask; });

    private async Task ConnectAndWaitForReadAsync()
    {
        _status.Set(Subsystems.Prosim, ConnectionState.Connected);
        for (var i = 0; i < 100 && !_store.Snapshot().HasReading; i++)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task OnConnect_ReadsEveryRow_AndTheVersion_WritesNothing()
    {
        using var service = Create();

        await ConnectAndWaitForReadAsync();

        var snapshot = _store.Snapshot();
        Assert.True(snapshot.HasReading);
        Assert.Equal("1.75.1", snapshot.ProsimVersion);
        Assert.Equal(5, snapshot.Items.Count);
        Assert.All(snapshot.Items, item => Assert.Equal(ProsimSetupStatus.Ok, item.Status));
        Assert.Equal(0, snapshot.MismatchCount);
        Assert.Empty(_writes);
        Assert.False(snapshot.Busy);
    }

    [Fact]
    public async Task OnConnect_WithAMismatch_ReportsIt_StillWritesNothing()
    {
        _prosim[ProsimSetupRecommendations.DoorLogic] = "true";
        using var service = Create();

        await ConnectAndWaitForReadAsync();

        var door = _store.Snapshot().Items.Single(item => item.DataRef == ProsimSetupRecommendations.DoorLogic);
        Assert.Equal(ProsimSetupStatus.Mismatch, door.Status);
        Assert.Equal("true", door.Actual);
        Assert.Equal(1, _store.Snapshot().MismatchCount);
        Assert.Empty(_writes);   // the pilot decides; the connection never writes
    }

    [Fact]
    public async Task OnConnect_CheckDisabled_ReadsNothing()
    {
        _prosimOptions.SetupCheckEnabled = false;
        using var service = Create();

        _status.Set(Subsystems.Prosim, ConnectionState.Connected);
        await Task.Delay(100);

        Assert.False(_store.Snapshot().HasReading);
        _gateway.Verify(g => g.QueryDataRefAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnConnect_UnknownName_IsNotPresent_NotAMismatch()
    {
        _prosim.Remove(ProsimSetupRecommendations.RefuelRate);   // gateway answers null
        using var service = Create();

        await ConnectAndWaitForReadAsync();

        var rate = _store.Snapshot().Items.Single(item => item.DataRef == ProsimSetupRecommendations.RefuelRate);
        Assert.Equal(ProsimSetupStatus.NotPresent, rate.Status);
        Assert.Equal(0, _store.Snapshot().MismatchCount);
    }

    [Fact]
    public async Task OnConnect_NoVersionFromGateway_LeavesTheStoreAlone()
    {
        // A dead gateway answers null for everything — that must not read as five missing options.
        _prosim.Clear();
        using var service = Create();

        _status.Set(Subsystems.Prosim, ConnectionState.Connected);
        await Task.Delay(150);

        Assert.False(_store.Snapshot().HasReading);
        Assert.Empty(_store.Snapshot().Items);
    }

    [Fact]
    public async Task Disconnect_ClearsTheStore_AndTheNextConnectReadsAgain()
    {
        using var service = Create();
        await ConnectAndWaitForReadAsync();

        _status.Set(Subsystems.Prosim, ConnectionState.Disconnected);
        Assert.False(_store.Snapshot().HasReading);

        _prosim[ProsimSetupRecommendations.DoorLogic] = "true";   // changed while ProSim was down
        await ConnectAndWaitForReadAsync();

        Assert.Equal(1, _store.Snapshot().MismatchCount);
    }

    [Fact]
    public async Task Apply_WritesOnlyTheMismatchedRows_ThenReadsBack()
    {
        _prosim[ProsimSetupRecommendations.DoorLogic] = "true";
        _prosim[ProsimSetupRecommendations.RefuelRate] = "Quick";
        using var service = Create();
        await ConnectAndWaitForReadAsync();

        var result = await service.ApplyRecommendedAsync();

        Assert.Equal(
            [(ProsimSetupRecommendations.DoorLogic, (object)false), (ProsimSetupRecommendations.RefuelRate, "Realistic")],
            _writes);
        Assert.Equal(["Door logic", "Refuelling rate"], result.Written);
        Assert.Empty(result.Failed);
        Assert.Equal("Set 2 options in ProSim.", result.Summary);
        Assert.All(_store.Snapshot().Items, item => Assert.Equal(ProsimSetupStatus.Ok, item.Status));
        Assert.False(_store.Snapshot().Busy);
    }

    [Fact]
    public async Task Apply_WriteAcceptedButAppliedLate_WaitsForTheReadBack_AndReportsWritten()
    {
        // Sim PC 2026-10-11: every write came back accepted, the read-back in the same
        // millisecond still read the old values, and the card said "ProSim did not accept"
        // while the next check read five Ok rows.
        _prosim[ProsimSetupRecommendations.DoorLogic] = "true";
        _prosim[ProsimSetupRecommendations.RefuelRate] = "Quick";
        _staleReadsAfterWrite = 2;
        using var service = Create();
        await ConnectAndWaitForReadAsync();

        var result = await service.ApplyRecommendedAsync();

        Assert.Equal(["Door logic", "Refuelling rate"], result.Written);
        Assert.Empty(result.Failed);
        Assert.Equal(2, _delays);
        Assert.All(_store.Snapshot().Items, item => Assert.Equal(ProsimSetupStatus.Ok, item.Status));
    }

    [Fact]
    public async Task Apply_SkipsIgnoredRows()
    {
        _prosim[ProsimSetupRecommendations.DoorLogic] = "true";
        _prosim[ProsimSetupRecommendations.AutomaticGroundPower] = "true";
        _prosimOptions.SetupCheckIgnore.Add("SYSTEM.CONFIG.CONFIG.DOORS");   // case-insensitive, as a hand edit might spell it
        using var service = Create();
        await ConnectAndWaitForReadAsync();

        var door = _store.Snapshot().Items.Single(item => item.DataRef == ProsimSetupRecommendations.DoorLogic);
        Assert.Equal(ProsimSetupStatus.Ignored, door.Status);
        Assert.Equal(1, _store.Snapshot().MismatchCount);

        var result = await service.ApplyRecommendedAsync();

        Assert.Single(_writes);
        Assert.Equal(ProsimSetupRecommendations.AutomaticGroundPower, _writes[0].Name);
        Assert.Equal(["Automatic ground power"], result.Written);
        Assert.Equal("true", _prosim[ProsimSetupRecommendations.DoorLogic]);   // untouched
    }

    [Fact]
    public async Task Apply_RejectedWrite_IsReportedAsFailed_AndTheRowStaysAmber()
    {
        _prosim[ProsimSetupRecommendations.DoorLogic] = "true";
        _rejectWrites = true;
        using var service = Create();
        await ConnectAndWaitForReadAsync();

        var result = await service.ApplyRecommendedAsync();

        Assert.Empty(result.Written);
        Assert.Equal(["Door logic"], result.Failed);
        Assert.Equal("ProSim did not accept: Door logic.", result.Summary);
        Assert.Equal(1, _store.Snapshot().MismatchCount);
    }

    [Fact]
    public async Task Apply_NothingToChange_WritesNothing()
    {
        using var service = Create();
        await ConnectAndWaitForReadAsync();

        var result = await service.ApplyRecommendedAsync();

        Assert.Empty(_writes);
        Assert.Equal("Nothing to change.", result.Summary);
    }

    [Fact]
    public async Task Apply_NotConnected_RefusesWithoutTouchingTheGateway()
    {
        using var service = Create();

        var result = await service.ApplyRecommendedAsync();

        Assert.Equal(["ProSim is not connected"], result.Failed);
        _gateway.Verify(g => g.WriteDataRefAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_NotConnected_ReturnsFalse_Connected_ReadsAgain()
    {
        using var service = Create();
        Assert.False(await service.RefreshAsync());

        await ConnectAndWaitForReadAsync();
        var first = _store.Snapshot().CheckedAtUtc;
        _prosim[ProsimSetupRecommendations.DatalinkLoadFuel] = "true";

        Assert.True(await service.RefreshAsync());

        Assert.Equal(1, _store.Snapshot().MismatchCount);
        Assert.True(_store.Snapshot().CheckedAtUtc >= first);
    }

    [Fact]
    public async Task Rows_FollowTheGsxFeatureSwitches()
    {
        _gsxOptions.Enabled = false;
        using var service = Create();

        await ConnectAndWaitForReadAsync();

        Assert.Equal(
            [ProsimSetupRecommendations.DatalinkLoadCargo, ProsimSetupRecommendations.DatalinkLoadFuel],
            _store.Snapshot().Items.Select(item => item.DataRef));
    }

    private sealed class FakeMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
