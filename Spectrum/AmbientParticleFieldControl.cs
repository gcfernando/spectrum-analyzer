// Developer :: > Gehan Fernando
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Spectrum;

// Subtle background layer: little glowing dots spawn per frequency band and fade out
internal sealed class AmbientParticleFieldControl : Control
{
    internal const int BandCount = 83;
    internal const int MaximumParticles = 128;

    private const byte ParticleLifetime = 36;

    private readonly byte[] _displayFrame = new byte[BandCount];
    // Fixed-size ring buffer of particles, so no allocations while animating
    private readonly Particle[] _particles = new Particle[MaximumParticles];
    // One brush reused for every dot, only its color changes per draw
    private readonly SolidBrush _particleBrush = new SolidBrush(Color.White);
    private int _nextParticle;
    private int _activeParticleCount;
    private int _frameNumber;
    private bool _disposed;
    private BarColorTheme _theme = BarColorThemes.Resolve("ClassicSmooth");

    // Custom painted + double buffered to avoid flicker since we redraw every frame
    internal AmbientParticleFieldControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(38, 35, 29);
    }

    // Test hooks so unit tests can peek at internal state
    internal int ActiveParticleCount => _activeParticleCount;

    internal bool IsDisposedForTesting => _disposed;

    // Swap the palette used to tint particles by level
    internal void SetTheme(BarColorTheme theme)
    {
        _theme = theme;
        Invalidate();
    }

    // Called per audio frame: ages existing particles, then maybe spawns new ones per band
    internal void SetSpectrum(byte[] values)
    {
        if (values == null)
            throw new ArgumentNullException(nameof(values));
        if (_disposed)
            return;

        // Copy what we got and zero the rest so a short array never leaves stale bands behind
        var count = Math.Min(values.Length, BandCount);
        Buffer.BlockCopy(values, 0, _displayFrame, 0, count);
        Array.Clear(_displayFrame, count, BandCount - count);

        var hasActiveLevel = false;
        for (var band = 0; band < BandCount; band++)
        {
            if (_displayFrame[band] != 0)
            {
                hasActiveLevel = true;
                break;
            }
        }

        // Pure silence: wipe everything immediately instead of letting dots linger
        if (!hasActiveLevel)
        {
            Array.Clear(_particles, 0, _particles.Length);
            _activeParticleCount = 0;
            _nextParticle = 0;
            Invalidate();
            return;
        }

        _frameNumber++;
        AgeParticles();
        for (var band = 0; band < BandCount; band++)
        {
            var level = _displayFrame[band];
            if (level != 0 && ShouldSpawn(_frameNumber, band, level))
                AddParticle(band, level);
        }

        Invalidate();
    }

    // Cheap fingerprint of particle state so tests can check determinism
    internal int GetParticleStateHashForTesting()
    {
        var hash = 17;
        for (var i = 0; i < _particles.Length; i++)
        {
            hash = (hash * 31) + _particles[i].Band;
            hash = (hash * 31) + _particles[i].Level;
            hash = (hash * 31) + _particles[i].Age;
            hash = (hash * 31) + _particles[i].Seed;
        }
        return hash;
    }

    // Position is derived from the seed, so each particle stays put while it fades and drifts up a bit
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        if (_activeParticleCount == 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        // Restore smoothing afterwards, the graphics object isn't ours
        var previousSmoothingMode = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            for (var i = 0; i < _particles.Length; i++)
            {
                var particle = _particles[i];
                if (particle.Age == 0)
                    continue;

                // Alpha and drift both follow remaining life; louder bands make bigger dots
                var life = particle.Age / (float)ParticleLifetime;
                var x = ((particle.Seed & 0xffff) / 65535f) * Math.Max(0, ClientSize.Width - 1);
                var baseY = ((particle.Seed >> 16) & 0xffff) / 65535f;
                var y = (baseY * Math.Max(0, ClientSize.Height - 1)) - ((1f - life) * ClientSize.Height * 0.18f);
                var size = 1f + ((particle.Level / 255f) * 4f);
                var color = GetLevelColor(particle.Level);
                _particleBrush.Color = Color.FromArgb((int)(life * 190f), color);
                e.Graphics.FillEllipse(_particleBrush, x - (size / 2f), y - (size / 2f), size, size);
            }
        }
        finally
        {
            e.Graphics.SmoothingMode = previousSmoothingMode;
        }
    }

    // Release the brush and drop particles; the flag guards late SetSpectrum calls
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _particleBrush.Dispose();
            Array.Clear(_particles, 0, _particles.Length);
            _activeParticleCount = 0;
        }

        base.Dispose(disposing);
    }

    // Ticks every particle's age down and recounts which ones are still alive (Age 0 = dead slot)
    private void AgeParticles()
    {
        _activeParticleCount = 0;
        for (var i = 0; i < _particles.Length; i++)
        {
            if (_particles[i].Age > 0)
                _particles[i].Age--;
            if (_particles[i].Age > 0)
                _activeParticleCount++;
        }
    }

    // Overwrites the oldest slot in the ring, only counting it as new if that slot was free
    private void AddParticle(int band, byte level)
    {
        if (_particles[_nextParticle].Age == 0)
            _activeParticleCount++;

        _particles[_nextParticle] = new Particle
        {
            Band = (byte)band,
            Level = level,
            Age = ParticleLifetime,
            Seed = Hash(_frameNumber, band, level)
        };
        _nextParticle = (_nextParticle + 1) % MaximumParticles;
    }

    // Louder band = higher spawn chance; hash gives a repeatable "random" roll without Random state
    private static bool ShouldSpawn(int frameNumber, int band, byte level) =>
        (Hash(frameNumber, band, level) & 0xff) <= level;

    // Low->mid for the quiet half, mid->high for the loud half
    private Color GetLevelColor(byte level)
    {
        var position = level / 255f;
        if (position < 0.5f)
            return Blend(_theme.Low, _theme.Mid, position * 2f);
        return Blend(_theme.Mid, _theme.High, (position - 0.5f) * 2f);
    }

    // Integer mixing hash (xorshift-multiply style) so the same inputs always give the same scatter
    private static int Hash(int frameNumber, int band, byte level)
    {
        unchecked
        {
            var value = (uint)(frameNumber * 0x45d9f3b) ^ ((uint)band * 0x119de1f3) ^ level;
            value ^= value >> 16;
            value *= 0x7feb352d;
            value ^= value >> 15;
            value *= 0x846ca68b;
            return (int)(value ^ (value >> 16));
        }
    }

    // Plain linear color mix, amount is 0..1
    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        from.A + (int)((to.A - from.A) * amount),
        from.R + (int)((to.R - from.R) * amount),
        from.G + (int)((to.G - from.G) * amount),
        from.B + (int)((to.B - from.B) * amount));

    // Seed packs x in the low 16 bits and y in the high 16 when drawing
    private struct Particle
    {
        public byte Band;
        public byte Level;
        public byte Age;
        public int Seed;
    }
}
