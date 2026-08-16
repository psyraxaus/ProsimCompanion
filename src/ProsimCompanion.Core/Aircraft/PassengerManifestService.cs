namespace ProsimCompanion.Core.Aircraft;

public sealed record PassengerManifestEntry(int SeatNumber, int Zone, string FirstName, string LastName);

/// <summary>
/// Passenger manifest generator: names for the BOOKED seat map (the OFP import / pax override
/// writes it; GSX boarding fills it). Names are drawn from the predecessor's curated pools —
/// first and last names independently random, no cultural pairing — seeded from the seat map
/// itself so the same flight keeps the same manifest across page visits, and a new map deals
/// new passengers. Read-only: never writes a dataref.
/// </summary>
public sealed class PassengerManifestService : IDisposable
{
    private static readonly int[] FallbackZoneCapacities = [24, 30, 36, 42];

    private readonly IDataRefSubscription<string?> _bookedSeatString;
    private readonly IDataRefSubscription<int>[] _zoneCapacities;
    private readonly object _lock = new();
    private string _manifestForMap = "";
    private IReadOnlyList<PassengerManifestEntry> _manifest = [];

    public PassengerManifestService(IProsimDataRefs prosim)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        _bookedSeatString = prosim.Subscribe(ProsimDataRefNames.PaxBookedString);
        _zoneCapacities =
        [
            prosim.Subscribe(ProsimDataRefNames.PaxZone1Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone2Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone3Capacity),
            prosim.Subscribe(ProsimDataRefNames.PaxZone4Capacity),
        ];
    }

    public void Dispose()
    {
        _bookedSeatString.Dispose();
        foreach (var zone in _zoneCapacities)
        {
            zone.Dispose();
        }
    }

    /// <summary>The manifest for the current booked map (regenerated lazily when it changes).</summary>
    public IReadOnlyList<PassengerManifestEntry> Manifest()
    {
        var mapString = _bookedSeatString.Value ?? "";
        lock (_lock)
        {
            if (mapString == _manifestForMap)
            {
                return _manifest;
            }

            var capacities = _zoneCapacities.Select(zone => zone.Value).ToArray();
            if (capacities.Sum() <= 0)
            {
                capacities = FallbackZoneCapacities;
            }

            _manifest = Generate(SeatMap.Parse(mapString), capacities);
            _manifestForMap = mapString;
            return _manifest;
        }
    }

    /// <summary>Pure generation core (public for tests): entries for every booked seat, zoned
    /// by the capacity boundaries, names dealt deterministically from the map itself.</summary>
    public static IReadOnlyList<PassengerManifestEntry> Generate(bool[] bookedMap, int[] zoneCapacities)
    {
        if (bookedMap.Length == 0)
        {
            return [];
        }

        // Deterministic per map: the same booked string always deals the same names.
        var random = new Random(StableHash(bookedMap));
        var entries = new List<PassengerManifestEntry>();
        var zoneStart = 0;
        var zone = 1;
        var zoneEnd = zoneCapacities.Length > 0 ? zoneCapacities[0] : bookedMap.Length;

        for (var seat = 0; seat < bookedMap.Length; seat++)
        {
            while (seat >= zoneEnd && zone < zoneCapacities.Length)
            {
                zoneStart = zoneEnd;
                zone++;
                zoneEnd = zoneStart + zoneCapacities[zone - 1];
            }
            if (bookedMap[seat])
            {
                entries.Add(new PassengerManifestEntry(
                    seat + 1,
                    zone,
                    FirstNames[random.Next(FirstNames.Length)],
                    Surnames[random.Next(Surnames.Length)]));
            }
        }
        return entries;
    }

    private static int StableHash(bool[] map)
    {
        var hash = 17;
        foreach (var seat in map)
        {
            hash = unchecked(hash * 31 + (seat ? 1 : 0));
        }
        return hash;
    }

    // The predecessor's curated pools: 100 first names / 200 surnames spanning Anglo,
    // Spanish/Portuguese, Chinese, Korean/Japanese, Indian/South Asian, Arabic/Middle East,
    // German/Dutch, French and Italian origins (subset of the EFB's ~5000-entry list).
    private static readonly string[] Surnames =
    [
        "Smith", "Jones", "Williams", "Brown", "Wilson", "Taylor", "Johnson", "Martin",
        "Anderson", "Thompson", "Davis", "Miller", "Moore", "Walker", "White", "Harris",
        "Robinson", "Clark", "Lewis", "Hall", "Young", "King", "Wright", "Scott",
        "Green", "Adams", "Baker", "Hill", "Carter", "Mitchell", "Roberts", "Turner",
        "Phillips", "Campbell", "Parker", "Evans", "Edwards", "Collins", "Stewart", "Morris",
        "Garcia", "Martinez", "Rodriguez", "Lopez", "Gonzalez", "Hernandez", "Perez", "Sanchez",
        "Ramirez", "Torres", "Flores", "Rivera", "Gomez", "Diaz", "Reyes", "Cruz",
        "Morales", "Ortiz", "Silva", "Costa",
        "Chen", "Wang", "Li", "Zhang", "Liu", "Yang", "Huang", "Zhao",
        "Wu", "Zhou", "Xu", "Sun", "Ma", "Zhu", "Hu", "Lin",
        "Guo", "He", "Gao", "Tang",
        "Kim", "Park", "Lee", "Tanaka", "Suzuki", "Sato", "Watanabe", "Yamamoto",
        "Nakamura", "Kobayashi", "Kato", "Ito", "Saito", "Choi", "Jung", "Yoon",
        "Hwang", "Han", "Oh", "Shin",
        "Singh", "Kumar", "Patel", "Sharma", "Verma", "Gupta", "Reddy", "Iyer",
        "Joshi", "Nair", "Das", "Rao", "Bhatt", "Banerjee", "Chatterjee", "Mehta",
        "Saxena", "Agarwal", "Kapoor", "Chopra",
        "Ali", "Khan", "Hassan", "Hussein", "Saleh", "Mahmoud", "Aziz", "Rahman",
        "Abbas", "Karim", "Yousef", "Fadel", "Najjar", "Haddad", "Khalil", "Mansour",
        "Sabri", "Khoury", "Ahmed", "Bakir",
        "Mueller", "Schmidt", "Fischer", "Weber", "Meyer", "Wagner", "Becker", "Schulz",
        "Hoffmann", "Schaefer", "Bauer", "Koch", "Richter", "Klein", "Wolf", "Schultz",
        "Krause", "Lange", "Hartmann", "Vogel",
        "Dupont", "Bernard", "Moreau", "Laurent", "Simon", "Michel", "Lefebvre", "Leroy",
        "Roux", "David", "Petit", "Robert", "Richard", "Durand", "Dubois", "Garnier",
        "Faure", "Rousseau", "Blanc", "Girard",
        "Rossi", "Ferrari", "Esposito", "Bianchi", "Romano", "Colombo", "Ricci", "Marino",
        "Greco", "Conti", "Russo", "Bruno", "Gallo", "Lombardi", "Moretti", "Barbieri",
        "Fontana", "Santoro", "Mariani", "Caruso",
    ];

    private static readonly string[] FirstNames =
    [
        "James", "John", "Robert", "Michael", "William", "David", "Richard", "Joseph",
        "Thomas", "Charles", "Christopher", "Daniel", "Matthew", "Anthony", "Mark", "Donald",
        "Steven", "Paul", "Andrew", "Joshua",
        "Emma", "Olivia", "Ava", "Isabella", "Sophia", "Mia", "Charlotte", "Amelia",
        "Harper", "Evelyn", "Abigail", "Emily", "Elizabeth", "Avery", "Sofia", "Ella",
        "Madison", "Scarlett", "Victoria", "Aria",
        "Mohamed", "Fatima", "Omar", "Aisha", "Hassan", "Ahmed", "Layla", "Yusuf",
        "Maryam", "Khalid",
        "Wei", "Fang", "Ming", "Hui", "Yu", "Jing", "Lei", "Hong", "Xin", "Zhao",
        "Raj", "Priya", "Anita", "Vikram", "Deepa", "Arjun", "Anjali", "Rohan",
        "Kavya", "Sanjay",
        "Lucas", "Marie", "Pierre", "Sophie", "Hans", "Anna", "Klaus", "Ingrid",
        "Lars", "Astrid",
        "Giuseppe", "Maria", "Paolo", "Lucia", "Marco", "Giulia", "Luca", "Francesca",
        "Antonio", "Sara",
        "Yuki", "Kenji", "Sakura", "Hiroshi", "Akiko", "Takeshi", "Naomi", "Daiki",
        "Mei", "Ren",
    ];
}
