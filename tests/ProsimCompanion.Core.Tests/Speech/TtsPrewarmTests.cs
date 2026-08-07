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

    [Fact]
    public void CollectRolePhrases_WarmsConfiguredCabinWording_AndLoadsheetLeadIn()
    {
        // The ACTUAL configured wording must be warmed — not a canned copy (the predecessor
        // warmed phrases its cabin service never spoke).
        var cabin = new CabinOptions { CabinSecureText = "Flight deck, QNH 1013 checked." };

        var pairs = TtsPrewarmService.CollectRolePhrases(cabin, new VoicesOptions(), "bm_george");

        Assert.Contains(("af_heart", "Flight deck, Q N H one zero one three checked."), pairs); // normalized
        Assert.Contains(("af_heart", cabin.CabinReadyText), pairs);
        Assert.Contains(("af_heart", cabin.BoardingDelayText), pairs);
        Assert.Contains(("am_onyx", "Loadsheet."), pairs);
        Assert.DoesNotContain(pairs, p => p.Phrase.Contains("QNH 1013", StringComparison.Ordinal));
    }

    [Fact]
    public void CollectRolePhrases_SkipsBlankVoices()
    {
        // A blank role voice renders in the FO voice, whose phrases the main warm covers.
        var pairs = TtsPrewarmService.CollectRolePhrases(
            new CabinOptions(), new VoicesOptions { Purser = "" }, "bm_george");

        Assert.DoesNotContain(pairs, p => p.Phrase.Contains("cabin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(("am_onyx", "Loadsheet."), pairs); // company still warmed
    }

    [Fact]
    public void CollectRolePhrases_SkipsVoicesEqualToTheFoVoice()
    {
        // Same voice = same cache namespace — warming again would be pure waste.
        var pairs = TtsPrewarmService.CollectRolePhrases(
            new CabinOptions(), new VoicesOptions { Purser = "bm_george", Company = "bm_george" }, "bm_george");

        Assert.Empty(pairs);
    }

    [Fact]
    public void CollectRolePhrases_SkipsBlankAndLiveTokenWording()
    {
        var cabin = new CabinOptions
        {
            CabinSecureText = "",
            CabinReadyText = "Cabin ready, {pax} on board.", // live token — cannot be warmed
        };

        var pairs = TtsPrewarmService.CollectRolePhrases(cabin, new VoicesOptions(), "bm_george");

        Assert.Equal(
            [("af_heart", cabin.BoardingDelayText), ("am_onyx", "Loadsheet.")],
            pairs);
    }
}
