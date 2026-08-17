using ProsimCompanion.Core.Aircraft;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

/// <summary>
/// The typed subscribe path's event contract (issue #91): <c>ValueChanged</c> must be raised
/// with the TYPED handle as sender — the handle the subscriber was actually given. The
/// original wrapper delegated the event accessor to the inner untyped table subscription, so
/// sender was the untyped object and the one handler casting sender
/// (<c>GsxAutomationService.OnIntRadChanged</c>) threw on every push: the INT/RAD
/// service-skip was dead in the field for an entire smoke test without a single visible
/// failure (the table's error sink contains subscriber exceptions).
/// </summary>
public sealed class TypedSubscriptionExtensionsTests
{
    [Fact]
    public void ValueChanged_SenderIsTheTypedHandle()
    {
        var dataRefs = new FakeProsimDataRefs();
        var typed = dataRefs.Subscribe(new DataRef<int>("system.switches.S_ASP_INTRAD", DataRefTier.Frequent, 1));

        object? observedSender = null;
        typed.ValueChanged += (sender, _) => observedSender = sender;
        dataRefs.LastSubscription!.RaisePush(0);

        Assert.Same(typed, observedSender);
        // The exact cast the INT/RAD handler performs — must not throw.
        var handle = Assert.IsAssignableFrom<IDataRefSubscription<int>>(observedSender);
        Assert.Equal(0, handle.Value);
    }

    [Fact]
    public void Value_FallsBackToTheDescriptor_UntilTheFirstPush()
    {
        var dataRefs = new FakeProsimDataRefs();
        var typed = dataRefs.Subscribe(new DataRef<int>("system.gates.B_GROUND", DataRefTier.Frequent, 7));

        Assert.Equal(7, typed.Value);
        dataRefs.LastSubscription!.RaisePush(0);
        Assert.Equal(0, typed.Value);
    }

    [Fact]
    public void Dispose_UnhooksTheInnerEvent_AndDisposesTheRegistration()
    {
        var dataRefs = new FakeProsimDataRefs();
        var typed = dataRefs.Subscribe(new DataRef<int>("system.switches.S_ASP2_INTRAD", DataRefTier.Frequent, 1));

        var raised = 0;
        typed.ValueChanged += (_, _) => raised++;
        typed.Dispose();
        dataRefs.LastSubscription!.RaisePush(0);

        Assert.Equal(0, raised);
        Assert.True(dataRefs.LastSubscription.Disposed);
    }

    private sealed class FakeProsimDataRefs : IProsimDataRefs
    {
        public FakeSubscription? LastSubscription { get; private set; }

        public IDataRefSubscription SubscribeDynamic(string name, DataRefTier tier)
            => LastSubscription = new FakeSubscription(name);

        public Task WriteAsync(string name, object? value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PressMomentaryAsync(string name, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeSubscription(string name) : IDataRefSubscription
    {
        public string Name => name;
        public object? RawValue { get; private set; }
        public bool IsStale => false;
        public DateTimeOffset? LastUpdatedUtc { get; private set; }
        public bool Disposed { get; private set; }

        public event EventHandler? ValueChanged;

        public T GetValue<T>(T fallback) => DataRefCoercion.Coerce(RawValue, fallback);

        public void RaisePush(object value)
        {
            RawValue = value;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() => Disposed = true;
    }
}
