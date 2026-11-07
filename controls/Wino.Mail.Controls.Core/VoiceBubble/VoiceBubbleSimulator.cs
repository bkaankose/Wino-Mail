namespace Wino.Mail.Controls.Core.VoiceBubble;

/// <summary>
/// Speech-like loudness bands for the voice bubble without a microphone: syllable envelopes timed from a
/// phrase, shaped across 48 bands with fast attack and slow release.
/// </summary>
public sealed class VoiceBubbleSimulator
{
    private const int BandCount = 48;
    private const double TranscriptionDelaySeconds = 0.25;
    private const double InterimLeadSeconds = 0.18;

    private readonly float[] _bands = new float[BandCount];
    private readonly List<Syllable> _syllables = [];
    private readonly List<(string Word, double Start, double End)> _words = [];
    private int _heardCount;
    private readonly Random _random = new(7);
    private int _index;
    private double _time;
    private double _end;
    private float _level;

    public ReadOnlySpan<float> Bands => _bands;

    public bool IsFinished { get; private set; } = true;

    public double Silence { get; private set; }

    /// <summary>The words a live transcription would have returned so far.</summary>
    public string HeardText { get; private set; } = string.Empty;

    /// <summary>The word being spoken, which a live transcription reports before it is final.</summary>
    public string InterimText { get; private set; } = string.Empty;

    public void Speak(string phrase)
    {
        _syllables.Clear();
        _words.Clear();
        _heardCount = 0;
        HeardText = string.Empty;
        InterimText = string.Empty;
        _index = 0;
        _time = 0;
        Silence = 0;
        IsFinished = false;

        var time = 0.35;
        foreach (var word in phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var letters = word.Count(char.IsLetter);
            var count = Math.Max(1, (int)Math.Round(letters / 3.2));
            var wordStart = time;
            for (var syllable = 0; syllable < count; syllable++)
            {
                var duration = 0.11 + _random.NextDouble() * 0.1;
                _syllables.Add(new Syllable(time, time + duration, 0.55 + _random.NextDouble() * 0.45, 0.3 + _random.NextDouble() * 0.7));
                time += duration + 0.025;
            }

            _words.Add((word, wordStart, time));

            time += word.Equals("and", StringComparison.OrdinalIgnoreCase) ? 0.22 : 0.06;
        }

        _end = time;
    }

    public void Step(double deltaSeconds)
    {
        _time += deltaSeconds;
        double envelope = 0;
        var formant = 0.5;

        while (_index < _syllables.Count && _time > _syllables[_index].End)
        {
            _index++;
        }

        if (_index < _syllables.Count && _time >= _syllables[_index].Start)
        {
            var current = _syllables[_index];
            envelope = current.Amplitude * Math.Sin(Math.PI * (_time - current.Start) / (current.End - current.Start));
            formant = current.Formant;
        }

        var heard = _heardCount;
        while (heard < _words.Count && _time >= _words[heard].End + TranscriptionDelaySeconds)
        {
            heard++;
        }

        if (heard != _heardCount)
        {
            _heardCount = heard;
            HeardText = string.Join(' ', _words.Take(heard).Select(static entry => entry.Word));
        }

        var pending = heard;
        while (pending < _words.Count && _time >= _words[pending].Start + InterimLeadSeconds)
        {
            pending++;
        }

        InterimText = pending == heard ? string.Empty : string.Join(' ', _words.Skip(heard).Take(pending - heard).Select(static entry => entry.Word));

        if (_time > _end)
        {
            IsFinished = true;
            Silence += deltaSeconds;
        }

        var target = (float)(0.025 + envelope);
        _level += (target - _level) * (target > _level ? 0.55f : 0.14f);

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

    private readonly record struct Syllable(double Start, double End, double Amplitude, double Formant);
}
