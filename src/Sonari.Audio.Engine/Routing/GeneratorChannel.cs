using Sonari.Audio.Core.Endpoints;
using Sonari.Audio.Core.Engine;

namespace Sonari.Audio.Engine.Routing;

/// <summary>
/// Canal cuya entrada se genera en el propio hilo de render (el reproductor de sonidos): no hay captura ni reloj
/// ajeno, así que no necesita ring buffer ni compensación de deriva.
/// </summary>
internal sealed class GeneratorChannel(ChannelControl control, int channels, RenderHandler generate)
    : ChannelStrip(control, channels)
{
    public override ChannelMetrics ReadMetrics() => new(
        Input: InputMeter.ReadAndReset(),
        PostFader: PostFaderMeter.ReadAndReset(),
        BufferMilliseconds: 0,
        DriftPpm: 0,
        Underruns: 0,
        IdleGaps: 0,
        OverrunSamples: 0,
        Discontinuities: 0);

    protected override void ReadInput(Span<float> buffer)
    {
        generate(buffer);
        InputMeter.Process(buffer, Channels);
    }
}
