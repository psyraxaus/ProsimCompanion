using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class TtsPrewarmTests
{
    [Fact]
    public void CollectPhrases_IncludesChecklistAndSopTexts_NormalizedAndDeduped()
    {
        var sop = new SopOptions();
        var checklists = new[]
        {
            new ChecklistDefinition
            {
                Checklist = "Before Start",
                Items =
                [
                    new ChecklistItemDefinition { Say = "Parking brake", ExpectedResponse = "set" },
                    new ChecklistItemDefinition { Say = "QNH 1013" },   // gets normalized
                ],
            },
        };

        var phrases = TtsPrewarmService.CollectPhrases(sop, checklists);

        Assert.Contains("Before Start checklist.", phrases);
        Assert.Contains("Before Start checklist complete.", phrases);
        Assert.Contains("Parking brake", phrases);
        Assert.Contains("set", phrases);
        Assert.Contains("Q N H one zero one three", phrases);   // AviationSpeech applied
        Assert.DoesNotContain("QNH 1013", phrases);
        Assert.Contains("V one", phrases);                      // SOP callout texts
        Assert.Contains("unstable, go around", phrases);
        Assert.Contains("gear still down", phrases);            // flow monitor
        Assert.Contains("ten thousand", phrases);               // altitude callouts
        Assert.Equal(phrases.Count, phrases.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CollectPhrases_SkipsLiveTokenTemplatesAndBlanks()
    {
        var sop = new SopOptions();
        sop.V1.Text = "V one is {v1}";   // live token — cannot be warmed
        sop.Rotate.Text = "";

        var phrases = TtsPrewarmService.CollectPhrases(sop, []);

        Assert.DoesNotContain(phrases, p => p.Contains('{', StringComparison.Ordinal));
        Assert.DoesNotContain("", phrases);
    }
}
