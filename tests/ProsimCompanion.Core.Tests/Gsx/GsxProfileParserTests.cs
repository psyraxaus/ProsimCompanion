using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Gsx.Profiles;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The GSX airport-profile readers, fed the shapes of the real EFHK (MK Studios) and
/// EGLL (iniBuilds) files that explained the W40 refusals of 2026-10-04.</summary>
public sealed class GsxProfileParserTests
{
    private const string EfhkIni = """
        [general]
        creator = Lufdhamsda
        version = 1.3.2

        [jetway_rootfloor_heights]
        jetway_32 = 3.5

        [gate 17]
        hasjetway = 1
        pushback = 3
        pushbacklabels = Facing NW on Taxi AD|Facing SE on Taxi AD
        maxwingspan = 42.199999999999996
        radiusleft = 21.099999999999998
        radiusright = 16.88
        airlinecodes =
        type = 9
        this_parking_pos = 60.3174818502104 24.9712516322986 -116.965074414063
        handlingtexture = AIRPRO,AVIATOR,AY
        parkingsystem = SafeDockTS24

        [gate w 40]
        hasjetway = 1
        pushback = 3
        pushbacklabels = Facing SW on Taxi AV|Facing NE on Taxi AT
        maxwingspan = 65.0
        radiusleft = 31.0
        radiusright = 24.8
        type = 10
        this_parking_pos = 60.3155884627228 24.9581529816402 137.464508056641
        pushbackleftpos = 60.31665712 24.95765133 -132.5354919
        pushbackrightpos = 60.31569183 24.95612174 47.46450806
        pushbackaddpos = [{'snap': False, 'approach': [(60.3, 24.9, 68.7), None], 'pos': (60.3166, 24.9570, 158.800003052), 'label': u'Facing South (V2)'}]

        [gate s 45a]
        pushback = 1
        type = 9

        [parking 401]
        pushback = 2
        type = 5

        [gate a 252_mars_252a]
        type = 10

        [deice stand 811 -rwy04-]
        radius = 50

        [none 101]
        type = 1
        """;

    private const string EfhkPy = """
        # -- coding: utf-8 --
        msfs_mode = 1
        icao = "efhk"

        Apron1E = CustomizedName("Apron 1E (Gates 5-23) | Gate #",1)
        Apron1W = CustomizedName("Apron 1W (Gates W34-W48) | Gate #§",3)
        Apron4 = CustomizedName("Apron 4 (Cargo, 401-411)  | Stand #", 7)

        @AlternativeStopPositions
        def custom_stops_1w4(aircraftData):
            distances = { 0: 0, 330: 2.3 }
            return Distance.fromMeters(distances.get(aircraftData.idMajor, 0))

        parkings = {
            GATE : {
                None : (),
                17 : (Apron1E, custom_stops_1w4),
            },

            GATE_W : {
                None : (),
                34 : (Apron1W, custom_stops_1w4),
                '34B' : (Apron1W, default_stop),
                40 : (Apron1W, custom_stops_1w4),
            },

            PARKING : {
                None : (),
                401 : (Apron4, default_stop),
                811 : (CustomizedName("Do Not Use  | Stand #", 9), default_stop),
                812 : (PresetNames(Nouse, "", "Stand"), ),
            },
        }
        """;

    [Fact]
    public void Ini_ReadsStandIdentitiesAndSkipsEverythingElse()
    {
        var profile = GsxIniProfileParser.Parse(EfhkIni);

        Assert.Equal("Lufdhamsda", profile.General["creator"]);
        var ids = profile.Stands.Select(s => (s.Identity, s.MarsId)).ToList();
        Assert.Contains((new ParkingIdentity(ParkingName.Gate, 17, ""), (string?)null), ids);
        Assert.Contains((new ParkingIdentity(ParkingName.GateW, 40, ""), (string?)null), ids);
        Assert.Contains((new ParkingIdentity(ParkingName.GateS, 45, "A"), (string?)null), ids);
        Assert.Contains((new ParkingIdentity(ParkingName.Parking, 401, ""), (string?)null), ids);
        Assert.Contains((new ParkingIdentity(ParkingName.GateA, 252, ""), (string?)"252A"), ids);
        Assert.Contains((new ParkingIdentity(ParkingName.None, 101, ""), (string?)null), ids);
        Assert.Equal(6, profile.Stands.Count); // de-ice and jetway tables are not stands
    }

    [Fact]
    public void Ini_W40_CarriesSizesJetwayAndPushback()
    {
        var stand = GsxIniProfileParser.Parse(EfhkIni).Stands.Single(s => s.Identity.Number == 40);
        var parking = GsxIniProfileParser.ToParking(stand);

        Assert.Equal(65.0, parking.MaxWingspanM);
        Assert.Equal(31.0, parking.RadiusLeftM);
        Assert.Equal(24.8, parking.RadiusRightM);
        Assert.True(parking.HasJetway);
        Assert.Equal(10, parking.TypeCode);
        Assert.Equal(137.46, parking.Pose!.Value.HeadingDeg, 2);
        Assert.Equal(ParkingDataSources.GsxIni, parking.Sources);

        var pushback = parking.Pushback!;
        Assert.Equal(PushbackDirections.Both, pushback.Allowed);
        Assert.Equal(3, pushback.Slots.Count);
        Assert.Equal(("Facing SW on Taxi AV", PushbackSlotKind.Left), (pushback.Slots[0].Label, pushback.Slots[0].Kind));
        Assert.Equal(227.46, pushback.Slots[0].FinalPose!.Value.HeadingDeg, 2); // −132.5 normalised
        Assert.Equal(("Facing NE on Taxi AT", PushbackSlotKind.Right), (pushback.Slots[1].Label, pushback.Slots[1].Kind));
        Assert.Equal(47.46, pushback.Slots[1].FinalPose!.Value.HeadingDeg, 2);
        Assert.Equal(("Facing South (V2)", PushbackSlotKind.Additional), (pushback.Slots[2].Label, pushback.Slots[2].Kind));
        Assert.Equal(158.8, pushback.Slots[2].FinalPose!.Value.HeadingDeg, 2);
    }

    [Fact]
    public void Ini_Gate17_UsesOperatorsAndEmptyAirlineList()
    {
        var parking = GsxIniProfileParser.ToParking(GsxIniProfileParser.Parse(EfhkIni).Stands.Single(s => s.Identity.Number == 17));

        Assert.Equal(["AIRPRO", "AVIATOR", "AY"], parking.HandlingOperators);
        Assert.Empty(parking.AirlineCodes);
        Assert.Equal("SafeDockTS24", parking.ParkingSystem);
    }

    [Fact]
    public void Ini_SingleDirectionStands_HaveOneSlotWithDefaultLabel()
    {
        var stands = GsxIniProfileParser.Parse(EfhkIni).Stands;
        var left = GsxIniProfileParser.ReadPushback(stands.Single(s => s.Identity.Number == 45))!;
        var right = GsxIniProfileParser.ReadPushback(stands.Single(s => s.Identity.Number == 401))!;

        Assert.Equal(PushbackDirections.Left, left.Allowed);
        Assert.Equal([GsxIniProfileParser.DefaultLeftLabel], left.Slots.Select(s => s.Label));
        Assert.Equal(PushbackDirections.Right, right.Allowed);
        Assert.Equal([GsxIniProfileParser.DefaultRightLabel], right.Slots.Select(s => s.Label));
    }

    [Fact]
    public void Py_W40_ResolvesToGate40_TheLetterIsNeverExpanded()
    {
        var py = GsxPyProfileParser.Parse(EfhkPy);

        var name = py.Resolve(new ParkingIdentity(ParkingName.GateW, 40, ""))!;
        Assert.Equal("Apron 1W (Gates W34-W48) | Gate 40", name.UiName);
        Assert.Equal("Gate 40", name.GateName);

        var suffixed = py.Resolve(new ParkingIdentity(ParkingName.GateW, 34, "B"))!;
        Assert.Equal("Gate 34B", suffixed.GateName);

        // A listed stand named by code the reader cannot run stays known but unnamed.
        Assert.Null(py.Resolve(new ParkingIdentity(ParkingName.Parking, 812, "")));
        Assert.Contains(new ParkingIdentity(ParkingName.Parking, 812, ""), py.ListedStands());

        // Inline CustomizedName works too.
        Assert.Equal("Stand 811", py.Resolve(new ParkingIdentity(ParkingName.Parking, 811, ""))!.GateName);

        // No template for a group the file does not mention.
        Assert.Null(py.Resolve(new ParkingIdentity(ParkingName.Dock, 1, "")));
    }

    [Fact]
    public void Py_GroupDefault_NamesUnlistedStands()
    {
        const string egll = """
            msfs_mode = 1
            trm_3 = CustomizedName("Terminal 3 | Gate  #§", 1)
            parkings = {
                GATE: {
                    None : (trm_3,),
                    "218R" : (trm_3,),
                },
                W_PARKING: {
                    None : (),
                    700 : (CustomizedName("Gate T | W Parking  #"),),
                },
            }
            """;
        var py = GsxPyProfileParser.Parse(egll);

        Assert.Equal("Gate  313", py.Resolve(new ParkingIdentity(ParkingName.Gate, 313, ""))!.GateName);
        Assert.Equal("Terminal 3 | Gate  218R", py.Resolve(new ParkingIdentity(ParkingName.Gate, 218, "R"))!.UiName);
        Assert.Equal("W Parking  700", py.Resolve(new ParkingIdentity(ParkingName.WParking, 700, ""))!.GateName);
        Assert.Null(py.Resolve(new ParkingIdentity(ParkingName.WParking, 701, "")));
    }

    [Fact]
    public void Py_HandlerScriptWithoutParkings_IsEmpty()
        => Assert.Empty(GsxPyProfileParser.Parse("def onEnterAirport(self):\n    pass\n").Groups);

    [Theory]
    [InlineData("EFHK-MKStudios.py", "EFHK", true)]
    [InlineData("efhk-mkstudios-gsx-luca.br.ini", "EFHK", true)]
    [InlineData("egll-24-iniBuilds.ini", "EGLL", true)]
    [InlineData("LGAV.ini", "LGAV", true)]
    [InlineData("EFHKX-other.ini", "EFHK", false)]
    [InlineData("kefhk.ini", "EFHK", false)]
    public void Locator_NameMatches(string file, string icao, bool expected)
        => Assert.Equal(expected, GsxProfileLocator.NameMatches(file, icao));

    [Fact]
    public void PyLiteral_ReadsGsxPushbackList()
    {
        var value = PyLiteral.Parse("[{'snap': False, 'approach': [(1.5, 2.5, 3.5), None], 'pos': (52.36, 13.50, -201.2), 'label': u'Facing South (V2)'}]");

        var list = Assert.IsType<List<object?>>(value);
        var dict = Assert.IsType<Dictionary<string, object?>>(list[0]);
        Assert.Equal(false, dict["snap"]);
        Assert.Equal("Facing South (V2)", dict["label"]);
        var pos = Assert.IsType<List<object?>>(dict["pos"]);
        Assert.Equal(-201.2, pos[2]);
    }
}
