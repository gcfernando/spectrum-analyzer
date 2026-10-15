// Developer :: > Gehan Fernando
using System;

namespace Spectrum.Dsp;

// Helpers for turning raw power into dB and then into a 0-255 value for the display
internal static class LevelScale
{
    // 0 dB is full scale, anything above just clamps to the top
    public const double CeilingDb = 0.0;
    // Below -72 dB we treat the bar as empty, nobody can see it anyway
    public const double FloorDb = -72.0;

    // Smallest power we bother taking a log of, avoids -inf
    public const double MinPower = 1e-30;

    // Sentinel for "nothing there" (silence or bad input)
    public const double SilenceDb = -300.0;

    // Power ratio to dB; the guards keep NaN, zero and infinity from blowing up the log
    public static double PowerToDb(double relativePower)
    {
        // Written as !(x > min) on purpose so NaN also lands here
        if (!(relativePower > MinPower))
        {
            return SilenceDb;
        }

        if (double.IsPositiveInfinity(relativePower))
        {
            return CeilingDb;
        }

        return 10.0 * Math.Log10(relativePower);
    }

    // Map dB between floor and ceiling onto 0..1, clamped
    public static double Normalize(double levelDb)
    {
        if (double.IsNaN(levelDb))
        {
            return 0.0;
        }

        var n = (levelDb - FloorDb) / (CeilingDb - FloorDb);
        return n <= 0.0 ? 0.0 : (n >= 1.0 ? 1.0 : n);
    }

    // Quantize to a byte so the UI can store levels compactly
    public static byte ToDisplayByte(double levelDb) => (byte)Math.Round(Normalize(levelDb) * 255.0);

    // Reverse of the byte quantization, so we can get back an approximate power from a stored level
    public static double DisplayByteToRelativePower(byte displayLevel)
    {
        // 0 means silent, skip the pow so we return a true zero
        if (displayLevel == 0)
        {
            return 0.0;
        }

        var db = FloorDb + ((CeilingDb - FloorDb) * displayLevel / 255.0);
        return Math.Pow(10.0, db / 10.0);
    }
}
