using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Tests;

public sealed class ControlPanelFormatProviderSelectorTests
{
    private static EndpointInfo Endpoint => new(
        "endpoint-id", "VG27AQL1A (NVIDIA High Definition Audio)", "VG27AQL1A",
        null, null, true);

    [Fact]
    public void Accepts_exact_or_prefixed_endpoint_title()
    {
        Assert.True(ControlPanelFormatProvider.IsExistingAudioPropertyTitleMatch("VG27AQL1A - 內容", Endpoint));
        Assert.True(ControlPanelFormatProvider.IsExistingAudioPropertyTitleMatch("VG27AQL1A", Endpoint));
    }

    [Fact]
    public void Rejects_generic_property_title_without_endpoint_match()
    {
        Assert.False(ControlPanelFormatProvider.IsExistingAudioPropertyTitleMatch("內容", Endpoint));
        Assert.False(ControlPanelFormatProvider.IsExistingAudioPropertyTitleMatch("Other endpoint - 內容", Endpoint));
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    [InlineData(0, true, false)]
    [InlineData(2, false, false)]
    public void Accepts_multiple_endpoint_elements_in_the_same_sound_window(
        int endpointMatchCount, bool isSoundMain, bool expected)
    {
        Assert.Equal(expected,
            ControlPanelFormatProvider.ShouldAcceptSoundWindow(endpointMatchCount, isSoundMain));
    }
}
