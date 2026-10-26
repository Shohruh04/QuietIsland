using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Windows.Threading;

namespace QuietIsland;

internal sealed class AudioService : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dispatcher _dispatcher;
    private MMDevice? _device;
    private bool _disposed;
    public event Action? Changed;
    public float Volume { get; private set; }
    public bool Muted { get; private set; }
    public AudioService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _enumerator.RegisterEndpointNotificationCallback(this);
        Attach();
    }
    private void Attach()
    {
        if (_disposed) return;
        if (_device != null) { _device.AudioEndpointVolume.OnVolumeNotification -= VolumeChanged; _device.Dispose(); _device = null; }
        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            Volume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            Muted = _device.AudioEndpointVolume.Mute;
            _device.AudioEndpointVolume.OnVolumeNotification += VolumeChanged;
        }
        catch (Exception ex) { AppSettings.Log(ex); }
    }
    private void VolumeChanged(AudioVolumeNotificationData data) => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        Volume = data.MasterVolume; Muted = data.Muted; Changed?.Invoke();
    });
    public void SetVolume(float value)
    {
        try { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0, 1); }
        catch (Exception ex) { AppSettings.Log(ex); }
    }
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) _dispatcher.BeginInvoke(() => { Attach(); Changed?.Invoke(); });
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string deviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    public void Dispose()
    {
        _disposed = true;
        _enumerator.UnregisterEndpointNotificationCallback(this);
        if (_device != null) { _device.AudioEndpointVolume.OnVolumeNotification -= VolumeChanged; _device.Dispose(); }
        _enumerator.Dispose();
    }
}
