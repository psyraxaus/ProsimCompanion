using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>One <c>optgroup</c> of the voice picker: "Kokoro · British English" and its rows.</summary>
public sealed record VoiceGroup(string Label, IReadOnlyList<VoiceEntry> Voices);
