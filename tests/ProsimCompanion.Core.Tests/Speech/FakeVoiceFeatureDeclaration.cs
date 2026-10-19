using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The declaration a test fake carries: one shared precedence, so a roster built from
/// fakes keeps the order the test listed them in (the roster's tie rule), an always-on gate
/// and no drawer rows.</summary>
internal static class FakeVoiceFeatureDeclaration
{
    public static VoiceFeatureDeclaration Plain { get; } = At(1000);

    public static VoiceFeatureDeclaration At(int precedence, string id = "fake") => new()
    {
        Precedence = precedence,
        Gate = VoiceFeatureGate.AlwaysOn,
        Description = new DescribedVoiceReference(id, $"voiceReference.group.{id}.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, []),
    };
}
