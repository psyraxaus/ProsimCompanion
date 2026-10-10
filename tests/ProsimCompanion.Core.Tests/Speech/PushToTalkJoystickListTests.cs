using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// Issue #165 (2026-10-11): the Voice FO settings page crashed the process by listing the
/// joysticks through winmm as it opened. The device list must be a cache that a page load
/// can read without entering the joystick layer; only an explicit scan probes.
/// </summary>
public sealed class PushToTalkJoystickListTests
{
    private static PushToTalkService Create()
    {
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(new SpeechOptions());
        monitor.Setup(m => m.OnChange(It.IsAny<Action<SpeechOptions, string?>>())).Returns(Mock.Of<IDisposable>());
        return new PushToTalkService(monitor.Object, NullLogger<PushToTalkService>.Instance);
    }

    [Fact]
    public void GetJoysticks_BeforeAnyScan_IsEmptyAndDoesNotProbe()
    {
        // The service is not started and no scan ran: a winmm probe would either throw on a
        // build agent without the joystick layer or return devices — both wrong here. The cache
        // answers at once with nothing.
        using var service = Create();
        IPttInputCapture capture = service;

        var before = capture.GetJoysticks();
        var again = capture.GetJoysticks();

        Assert.Empty(before);
        Assert.Same(before, again);
    }
}
