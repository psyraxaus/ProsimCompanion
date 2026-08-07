using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.Tests.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.TechLog;

public sealed class TechLogServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly TechLogOptions _options;
    private readonly TestTimeProvider _time = new();
    private readonly FakePhaseSource _phases = new();

    public TechLogServiceTests()
    {
        _dir = Directory.CreateTempSubdirectory("pc-techlog-").FullName;
        _options = new TechLogOptions { Path = Path.Combine(_dir, "techlog.json") };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // temp cleanup is best-effort
        }
    }

    private TechLogService Create()
    {
        var service = new TechLogService(
            OptionsSupport.Monitor(_options),
            SpeechTestSupport.TempEventLog(),
            _phases,
            NullLogger<TechLogService>.Instance,
            _time);
        service.Start();
        return service;
    }

    // ---- drafts + categories ----

    [Fact]
    public void NewDraft_FillsCategoryDerivedDefaults()
    {
        using var service = Create();
        var draft = service.NewDraft("manual", "  APU inoperative ", MelCategory.C);

        Assert.StartsWith("def-", draft.Id, StringComparison.Ordinal);
        Assert.Equal("2026-08-08", draft.RaisedDate);
        Assert.Equal("APU inoperative", draft.Title);
        Assert.Equal("MEL (SIM) CAT C", draft.MelReference);
        Assert.Equal(10, draft.RepairIntervalDays);
        Assert.Equal("2026-08-18", draft.DueDate);
        Assert.Equal(DefectStatus.Deferred, draft.Status);
        Assert.Equal("manual", draft.Source);
    }

    [Fact]
    public void NewDraft_BlankSourceBecomesManual()
    {
        using var service = Create();
        Assert.Equal("manual", service.NewDraft("", "x", MelCategory.C).Source);
    }

    [Theory]
    [InlineData(MelCategory.A, 3)]
    [InlineData(MelCategory.B, 3)]
    [InlineData(MelCategory.C, 10)]
    [InlineData(MelCategory.D, 120)]
    public void RepairDaysForCategory_UsesDefaults(MelCategory category, int expected)
    {
        using var service = Create();
        Assert.Equal(expected, service.RepairDaysForCategory(category));
    }

    [Fact]
    public void RepairDaysForCategory_HonoursOptionsOverride_AndClampsNegative()
    {
        _options.CategoryADays = 7;
        _options.CategoryDDays = -5;
        using var service = Create();
        Assert.Equal(7, service.RepairDaysForCategory(MelCategory.A));
        Assert.Equal(0, service.RepairDaysForCategory(MelCategory.D));
    }

    // ---- raise / rectify / remove ----

    [Fact]
    public void RaiseDefect_IsIdempotentById()
    {
        using var service = Create();
        var draft = service.NewDraft("manual", "APU inoperative", MelCategory.C);
        service.RaiseDefect(draft);

        var updated = service.NewDraft("manual", "APU inop (updated)", MelCategory.C);
        updated.Id = draft.Id;
        service.RaiseDefect(updated);

        var defect = Assert.Single(service.Defects);
        Assert.Equal("APU inop (updated)", defect.Title);
    }

    [Fact]
    public void RaiseDefect_AssignsIdWhenBlank()
    {
        using var service = Create();
        var raised = service.RaiseDefect(new TechLogDefect { Title = "x" });
        Assert.StartsWith("def-", raised.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void RectifyDefect_ClosesAndStamps()
    {
        using var service = Create();
        var defect = service.RaiseDefect(service.NewDraft("manual", "x", MelCategory.C));

        Assert.True(service.RectifyDefect(defect.Id));
        var rectified = Assert.Single(service.Defects);
        Assert.Equal(DefectStatus.Rectified, rectified.Status);
        Assert.Equal("2026-08-08", rectified.RectifiedDate);
        Assert.False(rectified.IsOpen);
        Assert.Empty(service.OpenDefects);
    }

    [Fact]
    public void RectifyDefect_FalseForUnknownOrAlreadyRectified()
    {
        using var service = Create();
        var defect = service.RaiseDefect(service.NewDraft("manual", "x", MelCategory.C));
        Assert.False(service.RectifyDefect("def-nope"));
        Assert.True(service.RectifyDefect(defect.Id));
        Assert.False(service.RectifyDefect(defect.Id));
    }

    [Fact]
    public void RemoveDefect_DeletesEntirely()
    {
        using var service = Create();
        var defect = service.RaiseDefect(service.NewDraft("manual", "x", MelCategory.C));
        Assert.True(service.RemoveDefect(defect.Id));
        Assert.False(service.RemoveDefect(defect.Id));
        Assert.Empty(service.Defects);
    }

    // ---- due dates ----

    [Fact]
    public void DaysRemaining_ExactParse()
    {
        using var service = Create();
        Assert.Equal(3, service.DaysRemaining(new TechLogDefect { DueDate = "2026-08-11" }));
        Assert.Equal(0, service.DaysRemaining(new TechLogDefect { DueDate = "2026-08-08" }));
        Assert.Equal(-2, service.DaysRemaining(new TechLogDefect { DueDate = "2026-08-06" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("08/08/2026")]
    [InlineData("2026-8-8")]
    public void DaysRemaining_MaxValueOnUnparseableDate(string dueDate)
    {
        using var service = Create();
        Assert.Equal(int.MaxValue, service.DaysRemaining(new TechLogDefect { DueDate = dueDate }));
    }

    [Fact]
    public void IsOverdue_OnlyForOpenPastDue()
    {
        using var service = Create();
        var overdue = new TechLogDefect { DueDate = "2026-08-01", Status = DefectStatus.Deferred };
        var rectified = new TechLogDefect { DueDate = "2026-08-01", Status = DefectStatus.Rectified };
        var future = new TechLogDefect { DueDate = "2026-08-20", Status = DefectStatus.Open };

        Assert.True(service.IsOverdue(overdue));
        Assert.False(service.IsOverdue(rectified));
        Assert.False(service.IsOverdue(future));
    }

    // ---- ordering ----

    [Fact]
    public void OpenDefects_OrderedMostDueFirst_DefectsMostRecentFirst()
    {
        using var service = Create();
        service.RaiseDefect(new TechLogDefect
        {
            Id = "def-1", RaisedDate = "2026-08-01", DueDate = "2026-08-20", Title = "later",
        });
        service.RaiseDefect(new TechLogDefect
        {
            Id = "def-2", RaisedDate = "2026-08-05", DueDate = "2026-08-10", Title = "sooner",
        });

        Assert.Equal(["def-2", "def-1"], service.OpenDefects.Select(d => d.Id));
        Assert.Equal(["def-2", "def-1"], service.Defects.Select(d => d.Id));
    }

    // ---- sector fold ----

    [Fact]
    public void FoldSectors_CountsOncePerSession()
    {
        using var service = Create();
        var defect = service.RaiseDefect(service.NewDraft("manual", "x", MelCategory.C));

        service.FoldSectors("session-20260808-100000");
        service.FoldSectors("session-20260808-100000"); // same session again — no-op
        Assert.Equal(1, service.Defects.Single().SectorsCarried);

        service.FoldSectors("session-20260809-100000");
        Assert.Equal(2, service.Defects.Single().SectorsCarried);

        service.RectifyDefect(defect.Id);
        service.FoldSectors("session-20260810-100000"); // closed items are not carried
        Assert.Equal(2, service.Defects.Single().SectorsCarried);
    }

    [Fact]
    public void FoldSectors_IgnoresBlankSession()
    {
        using var service = Create();
        service.RaiseDefect(service.NewDraft("manual", "x", MelCategory.C));
        service.FoldSectors(null);
        service.FoldSectors("  ");
        Assert.Equal(0, service.Defects.Single().SectorsCarried);
    }

    // ---- brief text ----

    [Fact]
    public void BuildBrief_SingleItem_ExactText()
    {
        using var service = Create();
        var open = new[]
        {
            new TechLogDefect
            {
                Title = "APU inoperative",
                MelReference = "MEL (SIM) CAT C",
                OperationalImplications = "ground air and external power required",
                DueDate = "2026-08-11",
                Status = DefectStatus.Deferred,
            },
        };

        Assert.Equal(
            "We're carrying one M E L item. APU inoperative, MEL (SIM) CAT C, "
            + "ground air and external power required, 3 days remaining.",
            service.BuildBrief(open));
    }

    [Fact]
    public void BuildBrief_MultipleItems_SkipsBlankFacts()
    {
        using var service = Create();
        var open = new[]
        {
            new TechLogDefect { Title = "First", DueDate = "2026-08-09" },
            new TechLogDefect { Title = "Second", MelReference = "MEL (SIM) 25-30", DueDate = "bad" },
        };

        Assert.Equal(
            "We're carrying 2 M E L items. First, 1 day remaining. "
            + "Second, MEL (SIM) 25-30, no rectification date.",
            service.BuildBrief(open));
    }

    [Theory]
    [InlineData("2026-08-07", "overdue by 1 day")]
    [InlineData("2026-08-05", "overdue by 3 days")]
    [InlineData("2026-08-08", "due today")]
    [InlineData("2026-08-09", "1 day remaining")]
    [InlineData("2026-08-12", "4 days remaining")]
    [InlineData("garbage", "no rectification date")]
    public void DaysPhrase_Pluralization(string dueDate, string expected)
    {
        using var service = Create();
        Assert.Equal(expected, service.DaysPhrase(new TechLogDefect { DueDate = dueDate }));
    }

    // ---- auto-rectify ----

    [Fact]
    public void CheckAutoRectify_ClosesOverdueOnlyWhenEnabled()
    {
        using var service = Create();
        service.RaiseDefect(new TechLogDefect { Id = "def-old", Title = "x", DueDate = "2026-08-01" });

        service.CheckAutoRectify(); // option off (default) — nothing happens
        Assert.Single(service.OpenDefects);

        _options.AutoRectifyOnDueDate = true;
        service.CheckAutoRectify();
        Assert.Empty(service.OpenDefects);
        Assert.Equal(DefectStatus.Rectified, service.Defects.Single().Status);
    }

    [Fact]
    public void CheckAutoRectify_RunsOnPreflightEdge()
    {
        _options.AutoRectifyOnDueDate = true;
        using var service = Create();
        service.RaiseDefect(new TechLogDefect { Id = "def-old", Title = "x", DueDate = "2026-08-01" });

        _phases.SetPhase(Core.Flight.FlightPhase.Preflight);
        Assert.Empty(service.OpenDefects);
    }

    // ---- persistence ----

    [Fact]
    public void Store_RoundTripsAcrossInstances()
    {
        using (var service = Create())
        {
            var defect = service.NewDraft("manual", "APU inoperative", MelCategory.B);
            defect.OperationalImplications = "ground air required";
            defect.Placard = "APU INOP";
            service.RaiseDefect(defect);
            service.FoldSectors("session-20260808-100000");
        }

        using var reloaded = Create();
        var loaded = Assert.Single(reloaded.Defects);
        Assert.Equal("APU inoperative", loaded.Title);
        Assert.Equal(MelCategory.B, loaded.Category);
        Assert.Equal("ground air required", loaded.OperationalImplications);
        Assert.Equal("APU INOP", loaded.Placard);
        Assert.Equal(1, loaded.SectorsCarried);
        Assert.Equal(["session-20260808-100000"], loaded.CountedSessions);
    }

    [Fact]
    public void Store_CorruptFileMovedAsideAndFreshStart()
    {
        File.WriteAllText(_options.Path, "{ this is not json ]");

        using var service = Create();
        Assert.Empty(service.Defects);
        Assert.Single(Directory.GetFiles(_dir, "techlog.json.corrupt-*.bak"));

        // And the store is writable again after recovery.
        service.RaiseDefect(service.NewDraft("manual", "x", MelCategory.C));
        Assert.True(File.Exists(_options.Path));
    }

    // ---- random wear ----

    [Fact]
    public void TryRandomWear_RaisesFromPoolAndFiresEvent()
    {
        var poolPath = Path.Combine(_dir, "wear-pool.json");
        File.WriteAllText(poolPath, """
            {
              "entries": [
                {
                  "title": "Forward galley oven 2 inoperative",
                  "category": "B",
                  "melReference": "MEL (SIM) 25-30",
                  "implications": "reduced hot service",
                  "placard": "FWD OVEN 2 INOP"
                }
              ]
            }
            """);
        _options.WearPoolPath = poolPath;

        using var service = Create();
        TechLogDefect? announced = null;
        service.WearRaised += d => announced = d;

        var raised = service.TryRandomWear("session-20260808-100000", () => 0.05, _ => 0);

        Assert.NotNull(raised);
        Assert.Equal("randomWear", raised.Source);
        Assert.Equal("Forward galley oven 2 inoperative", raised.Title);
        Assert.Equal(MelCategory.B, raised.Category);
        Assert.Equal("MEL (SIM) 25-30", raised.MelReference);
        Assert.Equal("reduced hot service", raised.OperationalImplications);
        Assert.Equal("FWD OVEN 2 INOP", raised.Placard);
        Assert.Same(raised, announced);
        Assert.Single(service.Defects);
    }

    [Fact]
    public void TryRandomWear_MissedRollOrEmptyPool_RaisesNothing()
    {
        var poolPath = Path.Combine(_dir, "wear-pool.json");
        File.WriteAllText(poolPath, """{ "entries": [ { "title": "x" } ] }""");
        _options.WearPoolPath = poolPath;

        using (var service = Create())
        {
            Assert.Null(service.TryRandomWear("s", () => 0.5, _ => 0)); // 0.5 > 0.08
            Assert.Empty(service.Defects);
        }

        _options.WearPoolPath = Path.Combine(_dir, "missing.json");
        using (var service = Create())
        {
            Assert.Null(service.TryRandomWear("s", () => 0.0, _ => 0));
        }
    }

    // ---- finalization step ----

    [Fact]
    public async Task FinalizationStep_FoldsSectors_SkipsWhenDisabled()
    {
        using var service = Create();
        service.RaiseDefect(service.NewDraft("manual", "x", MelCategory.C));
        ISessionFinalizationStep step = service;
        var context = new SessionFinalizationContext(@"C:\x\session-1.jsonl", "session-1");

        await step.RunAsync(context, CancellationToken.None);
        Assert.Equal(1, service.Defects.Single().SectorsCarried);

        _options.Enabled = false;
        await step.RunAsync(new SessionFinalizationContext(@"C:\x\session-2.jsonl", "session-2"), CancellationToken.None);
        Assert.Equal(1, service.Defects.Single().SectorsCarried);
    }
}
