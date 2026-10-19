namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The face of <see cref="OptionsDraft{TOptions}"/> a field renderer needs (candidate C3): the
/// editable clone, the out-of-the-box values, and the edit signal. Covariant and unconstrained
/// so one generic <c>OptionField</c> component can take any section's draft without carrying
/// the draft's <c>new()</c> / <see cref="IOptionSection"/> constraints into the page markup.
/// </summary>
/// <typeparam name="TOptions">The option section class.</typeparam>
public interface IOptionsDraft<out TOptions>
{
    /// <summary>The editable clone the page binds its inputs to.</summary>
    TOptions Value { get; }

    /// <summary>A fresh instance carrying every property default (shown beside numeric fields,
    /// the target of their reset button — ADR-0010).</summary>
    TOptions Defaults { get; }

    /// <summary>Raised after a field component wrote into <see cref="Value"/>: the page
    /// re-renders (the DirtyBar, the dependent fields' disabled state), as the one-line
    /// <c>@bind</c> used to make it do implicitly.</summary>
    event Action? Edited;

    /// <summary>Tells the page a field wrote into <see cref="Value"/>.</summary>
    void MarkEdited();
}
