using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Display;
using ProsimCompanion.Core.Profiles;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

public sealed partial class SettingsPagesRenderTests
{
    /// <summary>#234: a hand-written row editor must hold Save the way a typed field does.
    /// The screens table is rendered under the layout's validity tally (cascaded as
    /// <see cref="SettingsLayout"/> does) with one blank screen, and the tally must name the
    /// card while the bar's Save button is grey; a complete screen leaves both clear.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisplaySettings_BlankScreenRow_HoldsSave_ThroughTheValidityTally(bool blankRow)
    {
        var services = new ServiceCollection();
        Options(services, new DisplayOptions
        {
            Displays = blankRow
                ? [new DisplayEntry { Name = "" }]
                : [new DisplayEntry { Name = "Main TV", Role = DisplayRole.Main, DiagonalInches = 52, DistanceMetres = 1.5 }],
        });
        services.AddSingleton(new AircraftProfileService(new FakeDataRefs(), new FixedOptionsMonitor<AircraftProfilesOptions>(new AircraftProfilesOptions()), new OfpStore(), NullLogger<AircraftProfileService>.Instance));
        services.AddSingleton(Mock.Of<IFovSimNudge>());

        var validity = new FieldValidity();
        var parameters = new Dictionary<string, object?>
        {
            ["Validity"] = validity,
            ["PageParameters"] = new Dictionary<string, object?> { ["Section"] = "displays" },
        };
        var html = await RenderPageAsync<ValidityHost<ProsimCompanion.Web.Pages.DisplaySettings>>(services, "settings/display/displays", parameters);

        var bar = html[html.IndexOf("dirtybar-actions", StringComparison.Ordinal)..];
        if (blankRow)
        {
            Assert.Equal(["Your screens"], validity.BlockingFields);
            Assert.Contains("class=\"btn primary\" disabled", bar, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(0, validity.BlockingCount);
            Assert.DoesNotContain("class=\"btn primary\" disabled", bar, StringComparison.Ordinal);
        }
    }

    /// <summary>Stands in for <c>SettingsLayout</c>: cascades one <see cref="FieldValidity"/>
    /// (fixed, as the layout does) around the page under test so its fields and tables
    /// report into an instance the test can read.</summary>
    private sealed class ValidityHost<TPage> : ComponentBase
        where TPage : IComponent
    {
        [Parameter]
        public FieldValidity Validity { get; set; } = default!;

        [Parameter]
        public IDictionary<string, object?> PageParameters { get; set; } = new Dictionary<string, object?>();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<FieldValidity>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<FieldValidity>.Value), Validity);
            builder.AddComponentParameter(2, nameof(CascadingValue<FieldValidity>.IsFixed), true);
            builder.AddComponentParameter(3, nameof(CascadingValue<FieldValidity>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<TPage>(0);
                child.AddMultipleAttributes(1, PageParameters
                    .Where(pair => pair.Value is not null)
                    .Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value!)));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
