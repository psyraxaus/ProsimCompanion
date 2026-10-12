namespace ProsimCompanion.Web.Localization;

/// <summary>
/// The browser's language preferences for this circuit, captured from the first HTTP
/// request's <c>Accept-Language</c> header in <c>App.razor</c> (the only place the request
/// is in hand) and handed to the interactive tree through <c>Routes</c>. Scoped — one per
/// circuit, so a Japanese tablet and an English sim PC each get their own default until
/// the pilot pins a language (ADR-0014).
/// </summary>
public sealed class BrowserLanguageContext
{
    /// <summary>Language tags in preference order, lower-cased; empty when unknown.</summary>
    public IReadOnlyList<string> Languages { get; set; } = [];
}
