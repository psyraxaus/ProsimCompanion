namespace ProsimCompanion.Core.Aircraft;

/// <summary>One of the aircraft's doors as the diagnostics pages name it.</summary>
public sealed record DoorView(string Label, bool IsCargo, bool Open, bool Known);

/// <summary>
/// The eleven ProSim door datarefs behind one question, "which doors are open?", for the SIM
/// page's DOORS cell and the GSX page's hull markers. Subscribes lazily on the first read (a
/// constructor must not touch the feed, DI guard 2026-10-19) and shares the registrations
/// every other subscriber already holds, so it costs nothing on the wire.
/// </summary>
public sealed class DoorStateReader : IDisposable
{
    private static readonly (string Label, DataRef<bool> Ref, bool Cargo)[] Doors =
    [
        ("1L", ProsimDataRefNames.Door1L, false),
        ("1R", ProsimDataRefNames.Door1R, false),
        ("2L", ProsimDataRefNames.Door2L, false),
        ("2R", ProsimDataRefNames.Door2R, false),
        ("3L", ProsimDataRefNames.Door3L, false),
        ("3R", ProsimDataRefNames.Door3R, false),
        ("4L", ProsimDataRefNames.Door4L, false),
        ("4R", ProsimDataRefNames.Door4R, false),
        ("FWD CGO", ProsimDataRefNames.DoorCargoForward, true),
        ("AFT CGO", ProsimDataRefNames.DoorCargoAft, true),
        ("BULK", ProsimDataRefNames.DoorCargoBulk, true),
    ];

    private readonly IProsimDataRefs _dataRefs;
    private readonly object _gate = new();
    private IDataRefSubscription<bool>[]? _subscriptions;

    public DoorStateReader(IProsimDataRefs dataRefs)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        _dataRefs = dataRefs;
    }

    /// <summary>Every door, in the fixed order above. <c>Known</c> is false until the feed has
    /// pushed a value and while it is stale.</summary>
    public IReadOnlyList<DoorView> Read()
    {
        var subs = Ensure();
        var views = new DoorView[Doors.Length];
        for (var i = 0; i < Doors.Length; i++)
        {
            var sub = subs[i];
            var known = sub.LastUpdatedUtc is not null && !sub.IsStale;
            views[i] = new DoorView(Doors[i].Label, Doors[i].Cargo, known && sub.GetValueOr(false), known);
        }

        return views;
    }

    /// <summary>The open doors only.</summary>
    public IReadOnlyList<DoorView> OpenDoors() => [.. Read().Where(d => d.Open)];

    private IDataRefSubscription<bool>[] Ensure()
    {
        lock (_gate)
        {
            return _subscriptions ??= [.. Doors.Select(d => _dataRefs.Subscribe(d.Ref))];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_subscriptions is null)
            {
                return;
            }

            foreach (var sub in _subscriptions)
            {
                sub.Dispose();
            }

            _subscriptions = null;
        }
    }
}
