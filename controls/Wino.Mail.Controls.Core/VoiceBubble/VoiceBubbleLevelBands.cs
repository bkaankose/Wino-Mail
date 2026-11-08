namespace Wino.Mail.Controls.Core.VoiceBubble;

/// <summary>
/// Turns a live microphone level into the bubble's waveform bands, shaped like <see cref="VoiceBubbleSimulator"/>
/// so real speech and the demo look the same. Call <see cref="Step"/> once per rendered frame.
/// </summary>
public sealed class VoiceBubbleLevelBands
{
    private const int BandCount = 48;

    private readonly float[] _bands = new float[BandCount];
    private readonly Random _random = new(11);
    private double _time;
    private float _level;

    public ReadOnlySpan<float> Bands => _bands;

    public void Reset()
    {
        Array.Clear(_bands);
        _level = 0;
        _time = 0;
    }

    /// <param name="level">The microphone level from 0 to 1.</param>
    /// <param name="deltaSeconds">Seconds since the previous frame.</param>
    public void Step(double level, double deltaSeconds)
    {
        _time += deltaSeconds;

        var target = (float)(0.025 + Math.Clamp(level, 0, 1));
        _level += (target - _level) * (target > _level ? 0.55f : 0.14f);

        // The formant peak drifts slowly so a steady voice still moves.
        var formant = 0.55 + (0.35 * Math.Sin(_time * 2.1));
        for (var band = 0; band < BandCount; band++)
        {
            var x = band / (double)(BandCount - 1);
            var shape = Math.Exp(-Math.Pow(x - 0.16, 2) / 0.03)
                + 0.7 * formant * Math.Exp(-Math.Pow(x - 0.45, 2) / 0.025)
                + 0.25 * (1 - x)
                + 0.12 * Math.Exp(-Math.Pow(x - 0.75, 2) / 0.02);
            var noise = 0.75 + 0.25 * Math.Sin(band * 7.3 + _time * (13 + band));
            var value = (float)Math.Clamp(_level * shape * noise * 1.1 + 0.02 * _random.NextDouble(), 0, 1);
            _bands[band] += (value - _bands[band]) * (value > _bands[band] ? 0.5f : 0.12f);
        }
    }
}
