using System;

namespace Spectrum.Dsp;

/// <summary>Converts band power relative to a full-scale sine into the fixed −72 to 0 dBFS display range.</summary>
internal static class LevelScale
{
    public const double CeilingDb = 0.0;
    public const double FloorDb = -72.0;

    /// <summary>Power treated as digital silence to avoid taking log10 of zero.</summary>
    public const double MinPower = 1e-30;

    public const double SilenceDb = -300.0;

    public static double PowerToDb(double relativePower)
    {
        // Treat NaN, non-positive, and near-zero power as silence.
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

    /// <summary>Returns a finite normalized height in [0, 1].</summary>
    public static double Normalize(double levelDb)
    {
        if (double.IsNaN(levelDb))
        {
            return 0.0;
        }

        var n = (levelDb - FloorDb) / (CeilingDb - FloorDb);
        return n <= 0.0 ? 0.0 : (n >= 1.0 ? 1.0 : n);
    }

    /// <summary>Quantizes normalized height to the published spectrum's 0–255 byte range.</summary>
    public static byte ToDisplayByte(double levelDb) => (byte)Math.Round(Normalize(levelDb) * 255.0);

    /// <summary>Converts a published display level back to its quantized relative-power estimate.</summary>
    public static double DisplayByteToRelativePower(byte displayLevel)
    {
        if (displayLevel == 0)
        {
            return 0.0;
        }

        var db = FloorDb + ((CeilingDb - FloorDb) * displayLevel / 255.0);
        return Math.Pow(10.0, db / 10.0);
    }
}
