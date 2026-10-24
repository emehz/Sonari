using Sonari.Audio.Core.Effects;

namespace Sonari.Audio.Effects;

/// <summary>
/// Reverberación Freeverb (Jezar at Dreampoint, dominio público): por canal, 8 filtros en peine con amortiguación en
/// paralelo seguidos de 4 pasatodo en serie. Los tiempos del original (a 44,1 kHz) se escalan a la frecuencia del motor
/// y el canal derecho usa tiempos 23 muestras más largos para que las dos colas no coincidan (estéreo).
/// La reverberación se suma a la voz original, que no pierde nivel.
/// </summary>
internal sealed class Reverb : VoiceEffect
{
    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTunings = [556, 441, 341, 225];
    private const int StereoSpread = 23;
    private const float FixedGain = 0.015f;
    private const float ScaleWet = 3;
    private const float ScaleDamping = 0.4f;
    private const float ScaleRoom = 0.28f;
    private const float OffsetRoom = 0.7f;
    private const float AllpassFeedback = 0.5f;

    private readonly Comb[][] _combs;
    private readonly Allpass[][] _allpasses;
    private float _wet;

    public Reverb(int sampleRate, int channels)
        : base(sampleRate, channels)
    {
        double scale = sampleRate / 44100.0;
        _combs = new Comb[channels][];
        _allpasses = new Allpass[channels][];
        for (int c = 0; c < channels; c++)
        {
            int spread = (c % 2) * StereoSpread;
            _combs[c] = CombTunings.Select(t => new Comb((int)Math.Round((t + spread) * scale))).ToArray();
            _allpasses[c] = AllpassTunings.Select(t => new Allpass((int)Math.Round((t + spread) * scale))).ToArray();
        }

        Configure(new ReverbSettings());
    }

    public void Configure(ReverbSettings settings)
    {
        float feedback = (settings.RoomSize * ScaleRoom) + OffsetRoom;
        float damping = settings.Damping * ScaleDamping;
        foreach (Comb[] combs in _combs)
        {
            foreach (Comb comb in combs)
            {
                comb.Feedback = feedback;
                comb.Damping = damping;
            }
        }

        _wet = settings.Mix * ScaleWet;
    }

    public override void Reset()
    {
        foreach (Comb[] combs in _combs)
        {
            foreach (Comb comb in combs)
            {
                comb.Clear();
            }
        }

        foreach (Allpass[] allpasses in _allpasses)
        {
            foreach (Allpass allpass in allpasses)
            {
                allpass.Clear();
            }
        }
    }

    public override void Process(Span<float> interleaved)
    {
        int channels = Channels;
        for (int offset = 0; offset + channels <= interleaved.Length; offset += channels)
        {
            // Como en Freeverb, todas las colas parten de la suma de los canales.
            float input = 0;
            for (int c = 0; c < channels; c++)
            {
                input += interleaved[offset + c];
            }

            input *= FixedGain;

            for (int c = 0; c < channels; c++)
            {
                float sum = 0;
                Comb[] combs = _combs[c];
                for (int i = 0; i < combs.Length; i++)
                {
                    sum += combs[i].Process(input);
                }

                Allpass[] allpasses = _allpasses[c];
                for (int i = 0; i < allpasses.Length; i++)
                {
                    sum = allpasses[i].Process(sum);
                }

                interleaved[offset + c] += sum * _wet;
            }
        }
    }

    private sealed class Comb(int length)
    {
        private readonly float[] _buffer = new float[Math.Max(1, length)];
        private int _index;
        private float _store;

        public float Feedback { get; set; }

        public float Damping { get; set; }

        public float Process(float input)
        {
            float output = _buffer[_index];
            _store = (output * (1 - Damping)) + (_store * Damping);
            if (MathF.Abs(_store) < 1e-20f)
            {
                _store = 0;
            }

            _buffer[_index] = input + (_store * Feedback);
            if (++_index == _buffer.Length)
            {
                _index = 0;
            }

            return output;
        }

        public void Clear()
        {
            Array.Clear(_buffer);
            _store = 0;
            _index = 0;
        }
    }

    private sealed class Allpass(int length)
    {
        private readonly float[] _buffer = new float[Math.Max(1, length)];
        private int _index;

        public float Process(float input)
        {
            float buffered = _buffer[_index];
            float stored = input + (buffered * AllpassFeedback);
            _buffer[_index] = MathF.Abs(stored) < 1e-20f ? 0 : stored;
            if (++_index == _buffer.Length)
            {
                _index = 0;
            }

            return buffered - input;
        }

        public void Clear()
        {
            Array.Clear(_buffer);
            _index = 0;
        }
    }
}
