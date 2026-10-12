namespace ProsimCompanion.Web.Components;

/// <summary>
/// What the page knows about the browser it is rendered for, cascaded from <see cref="Routes"/>
/// (set once from the first HTTP request's remote address; the circuit keeps it). Today one
/// fact: whether the browser runs on the sim PC itself, which is the only place a Windows
/// dialog from the desktop shell can be seen (owner decision 2026-10-13, option 3).
/// </summary>
/// <param name="IsLocal">The browser is on this PC (loopback or one of its own addresses).</param>
public sealed record ClientContext(bool IsLocal);
