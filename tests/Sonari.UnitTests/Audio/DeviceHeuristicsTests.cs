using Sonari.Audio.Core.Devices;
using Sonari.Audio.Devices;

namespace Sonari.UnitTests.Audio;

public class AudioSystemCapabilitiesTests
{
    [Theory]
    [InlineData(10, 18363, FeatureSupport.Unavailable)]   // Windows 10 1909
    [InlineData(10, 19041, FeatureSupport.Probable)]      // Windows 10 2004
    [InlineData(10, 19045, FeatureSupport.Probable)]      // Windows 10 22H2
    [InlineData(10, 20348, FeatureSupport.Supported)]     // Windows Server 2022
    [InlineData(10, 22631, FeatureSupport.Supported)]     // Windows 11 23H2
    [InlineData(6, 9600, FeatureSupport.Unavailable)]     // Windows 8.1
    public void ProcessLoopback_DependsOnBuild(int major, int build, FeatureSupport expected)
    {
        var capabilities = AudioSystemCapabilities.FromOsVersion(new Version(major, 0, build, 0));

        Assert.Equal(expected, capabilities.ProcessLoopback);
        Assert.False(string.IsNullOrWhiteSpace(capabilities.ProcessLoopbackDetail));
    }
}

public class KnownVirtualDevicesTests
{
    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)")]
    [InlineData("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)")]
    [InlineData("Line 1 (Virtual Audio Cable)")]
    [InlineData("Micrófono (Voicemod Virtual Audio Device (WDM))")]
    public void RecognisesCommonVirtualCables(string name)
    {
        Assert.True(KnownVirtualDevices.IsLikelyVirtual(name));
    }

    [Theory]
    [InlineData("Altavoces (Realtek(R) Audio)")]
    [InlineData("Micrófono (USB Audio Device)")]
    [InlineData("Auriculares (HyperX Cloud II)")]
    [InlineData("")]
    [InlineData(null)]
    public void DoesNotFlagPhysicalDevices(string? name)
    {
        Assert.False(KnownVirtualDevices.IsLikelyVirtual(name));
    }
}
