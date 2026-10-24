using Sonari.Audio.Core.Devices;

namespace Sonari.UnitTests.Audio;

public class SonariVirtualDeviceTests
{
    private static AudioEndpointInfo Output(string id, string name, bool isVirtual, EndpointState state = EndpointState.Active) =>
        new(id, name, EndpointFlow.Render, state, IsDefault: false, IsDefaultCommunications: false, isVirtual);

    private static readonly AudioEndpointInfo Headphones = Output("hp", "Auriculares (Logitech G733 Gaming Headset)", false);
    private static readonly AudioEndpointInfo VbCable = Output("vb", "CABLE Input (VB-Audio Virtual Cable)", true);
    private static readonly AudioEndpointInfo Sonari = Output("sn", "Sonari Virtual Output (Sonari Virtual Audio)", true);

    [Theory]
    [InlineData("Sonari Virtual Output (Sonari Virtual Audio)", true)]
    [InlineData("Sonari Virtual Microphone (Sonari Virtual Audio)", true)]
    [InlineData("Mi mezcla (Sonari Virtual Audio)", true)]          // Endpoint renombrado por el usuario.
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", false)]
    [InlineData("Sonari Virtual Output", false)]                    // Sin el dispositivo entre paréntesis.
    public void IsSonari_UsesTheDeviceName(string name, bool expected)
    {
        Assert.Equal(expected, SonariVirtualDevice.IsSonari(Output("x", name, true)));
    }

    [Fact]
    public void NothingChosenYet_PicksTheSonariDriver()
    {
        Assert.Equal("sn", SonariVirtualDevice.AutoSelectOutput([Headphones, VbCable, Sonari], null, userChose: false)?.Id);
    }

    [Fact]
    public void UserChoseDisabled_IsRespected()
    {
        Assert.Null(SonariVirtualDevice.AutoSelectOutput([Headphones, Sonari], null, userChose: true));
    }

    [Fact]
    public void SavedCableStillPresent_IsRespected()
    {
        Assert.Null(SonariVirtualDevice.AutoSelectOutput([Headphones, VbCable, Sonari], "vb", userChose: true));
    }

    [Fact]
    public void SavedCableNoLongerExists_FallsBackToTheSonariDriver()
    {
        // P. ej. el usuario desinstaló VB-CABLE después de instalar el driver de Sonari.
        Assert.Equal("sn", SonariVirtualDevice.AutoSelectOutput([Headphones, Sonari], "vb", userChose: true)?.Id);
    }

    [Fact]
    public void WithoutTheSonariDriver_NothingIsPicked()
    {
        Assert.Null(SonariVirtualDevice.AutoSelectOutput([Headphones, VbCable], null, userChose: false));
        Assert.Null(SonariVirtualDevice.AutoSelectOutput([Headphones], "vb", userChose: true));
    }

    private static AudioEndpointInfo Input(string id, string name, bool isVirtual, bool isDefault = false, bool isComms = false) =>
        new(id, name, EndpointFlow.Capture, EndpointState.Active, isDefault, isComms, isVirtual);

    [Fact]
    public void EffectiveDefault_IsTheWindowsDefaultWhenItIsNotSonari()
    {
        AudioEndpointInfo[] inputs =
        [
            Input("mic", "Micrófono (Leviosa Max)", false, isDefault: true),
            Input("sn", "Sonari Virtual Microphone (Sonari Virtual Audio)", true),
        ];

        Assert.Equal("mic", SonariVirtualDevice.EffectiveDefault(inputs)?.Id);
    }

    [Fact]
    public void EffectiveDefault_SkipsSonariAndUsesTheCommunicationsDefault()
    {
        // El caso que provocaba el bucle: el micrófono de Sonari como entrada predeterminada de Windows.
        AudioEndpointInfo[] inputs =
        [
            Input("sn", "Sonari Virtual Microphone (Sonari Virtual Audio)", true, isDefault: true),
            Input("usb", "Micrófono (USB Audio Device)", false),
            Input("hd", "Microphone (High Definition Audio Device)", false, isComms: true),
        ];

        Assert.Equal("hd", SonariVirtualDevice.EffectiveDefault(inputs)?.Id);
    }

    [Fact]
    public void EffectiveDefault_FallsBackToTheFirstRealDevice()
    {
        AudioEndpointInfo[] inputs =
        [
            Input("sn", "Sonari Virtual Microphone (Sonari Virtual Audio)", true, isDefault: true, isComms: true),
            Input("vb", "CABLE Output (VB-Audio Virtual Cable)", true),
            Input("hd", "Microphone (High Definition Audio Device)", false),
        ];

        Assert.Equal("hd", SonariVirtualDevice.EffectiveDefault(inputs)?.Id);
    }

    [Fact]
    public void EffectiveDefault_IsNullWhenOnlySonariExists()
    {
        AudioEndpointInfo[] inputs = [Input("sn", "Sonari Virtual Microphone (Sonari Virtual Audio)", true, isDefault: true)];

        Assert.Null(SonariVirtualDevice.EffectiveDefault(inputs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Microphone (High Definition Audio Device)")]
    public void IsSonariName_RejectsOtherNames(string? name)
    {
        Assert.False(SonariVirtualDevice.IsSonariName(name));
    }

    [Fact]
    public void DisabledDriverOrMicrophoneSide_IsNeverPicked()
    {
        AudioEndpointInfo disabled = Output("sn", Sonari.Name, true, EndpointState.Disabled);
        AudioEndpointInfo microphone = new("mic", "Sonari Virtual Microphone (Sonari Virtual Audio)", EndpointFlow.Capture,
            EndpointState.Active, IsDefault: false, IsDefaultCommunications: false, IsLikelyVirtual: true);

        Assert.Null(SonariVirtualDevice.AutoSelectOutput([Headphones, disabled], null, userChose: false));
        Assert.Null(SonariVirtualDevice.AutoSelectOutput([Headphones, microphone], null, userChose: false));
    }
}
