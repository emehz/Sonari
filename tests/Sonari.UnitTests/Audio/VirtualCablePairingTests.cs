using Sonari.Audio.Core.Devices;

namespace Sonari.UnitTests.Audio;

public class VirtualCablePairingTests
{
    private static AudioEndpointInfo Endpoint(string id, string name, EndpointFlow flow, bool isVirtual) =>
        new(id, name, flow, EndpointState.Active, IsDefault: false, IsDefaultCommunications: false, isVirtual);

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable")]
    [InlineData("Altavoces (Realtek(R) Audio)", "Realtek(R) Audio")]
    [InlineData("Sin paréntesis", "Sin paréntesis")]
    public void DeviceFamily_IsTheLastParenthesis(string name, string expected)
    {
        Assert.Equal(expected, VirtualCablePairing.DeviceFamily(name));
    }

    [Fact]
    public void FindsTheCaptureSideOfVbCable()
    {
        var render = Endpoint("r1", "CABLE Input (VB-Audio Virtual Cable)", EndpointFlow.Render, true);
        AudioEndpointInfo[] captures =
        [
            Endpoint("c1", "Micrófono (Leviosa Max)", EndpointFlow.Capture, false),
            Endpoint("c2", "CABLE Output (VB-Audio Virtual Cable)", EndpointFlow.Capture, true),
        ];

        Assert.Equal("c2", VirtualCablePairing.FindCaptureSide(render, captures)?.Id);
    }

    [Fact]
    public void FindsTheMicrophoneOfTheSonariDriver()
    {
        // Con el driver propio, los avisos de realimentación y la indicación para Discord salen de este emparejamiento.
        var render = Endpoint("r1", "Sonari Virtual Output (Sonari Virtual Audio)", EndpointFlow.Render, true);
        AudioEndpointInfo[] captures =
        [
            Endpoint("c1", "CABLE Output (VB-Audio Virtual Cable)", EndpointFlow.Capture, true),
            Endpoint("c2", "Sonari Virtual Microphone (Sonari Virtual Audio)", EndpointFlow.Capture, true),
        ];

        Assert.Equal("c2", VirtualCablePairing.FindCaptureSide(render, captures)?.Id);
    }

    [Fact]
    public void PhysicalHeadset_IsNotTreatedAsACable()
    {
        // Un auricular real también tiene salida y micro con el mismo nombre de dispositivo, pero no es un cable.
        var render = Endpoint("r1", "Altavoces (Logitech G733 Gaming Headset)", EndpointFlow.Render, false);
        AudioEndpointInfo[] captures = [Endpoint("c1", "Micrófono (Logitech G733 Gaming Headset)", EndpointFlow.Capture, false)];

        Assert.Null(VirtualCablePairing.FindCaptureSide(render, captures));
    }
}
