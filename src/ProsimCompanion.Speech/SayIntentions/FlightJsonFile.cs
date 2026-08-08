using System.Text;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// Tolerant reader for the SayIntentions client's flight.json. File.ReadAllText opens with
/// FileShare.Read, which denies the SayIntentions client WRITE access to its own file for the
/// duration of the read — at the service's 1 Hz poll that contention window recurs every
/// second and the client sees its file as locked. All flight.json reads go through here:
/// share ReadWrite+Delete so our read never denies the writer anything, and no handle
/// outlives the call.
/// </summary>
internal static class FlightJsonFile
{
    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
