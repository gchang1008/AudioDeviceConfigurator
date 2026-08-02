using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator.Tests;

public sealed class CoreAudioInteropTests
{
    [Fact]
    public void Audio_render_client_uses_the_Windows_SDK_interface_id()
    {
        Assert.Equal(
            new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),
            typeof(CoreAudio.IAudioRenderClient).GUID);
    }

    [Fact]
    public void Audio_render_client_get_buffer_preserves_hresult_and_returns_pointer_out()
    {
        var method = typeof(CoreAudio.IAudioRenderClient).GetMethod("GetBuffer")!;

        Assert.Equal(typeof(int), method.ReturnType);
        Assert.Equal(typeof(IntPtr).MakeByRefType(), method.GetParameters()[1].ParameterType);
        Assert.True(method.GetParameters()[1].IsOut);
    }
}
