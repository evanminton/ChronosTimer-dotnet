using ChronosTimer.App.Pages;
using ChronosTimer.Audio;

namespace ChronosTimer.App;

public class App : Application
{
    public App()
    {
        UserAppTheme = AppTheme.Dark;
        AppHost.Initialize();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var tabs = new TabbedPage { Title = "Chronos Timer", BarBackgroundColor = Ui.Panel, UnselectedTabColor = Ui.Muted, SelectedTabColor = Ui.Accent };
        tabs.Children.Add(new NavigationPage(new TimerPage()) { Title = "Timer", BarBackgroundColor = Ui.Background, BarTextColor = Ui.Text });
        tabs.Children.Add(new NavigationPage(new SettingsPage()) { Title = "Settings", BarBackgroundColor = Ui.Background, BarTextColor = Ui.Text });
        tabs.Children.Add(new NavigationPage(new ControlPage()) { Title = "Control", BarBackgroundColor = Ui.Background, BarTextColor = Ui.Text });
        var window = new Window(tabs) { Title = "Chronos Timer", Width = 1000, Height = 760 };
        window.Stopped += (_, _) => AppHost.SaveNow();
        window.Destroying += (_, _) => { AppHost.SaveNow(); AppHost.Host.Dispose(); };
        return window;
    }
}

/// <summary>The one timer host of the app, with settings persistence.</summary>
internal static class AppHost
{
    private static TimerHost? _host;
    private static System.Timers.Timer? _saveTimer;

    public static TimerHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

    public static string SettingsPath => Path.Combine(FileSystem.AppDataDirectory, SettingsFile.FileName);

    public static void Initialize()
    {
        if (_host is not null) return;
#if ANDROID
        IAudioBackend audio = new AndroidAudioBackend();
#else
        IAudioBackend audio = AudioBackends.CreateDefault();
#endif
#if IOS || MACCATALYST
        try
        {
            var session = AVFoundation.AVAudioSession.SharedInstance();
            session.SetCategory(AVFoundation.AVAudioSessionCategory.PlayAndRecord,
                AVFoundation.AVAudioSessionCategoryOptions.DefaultToSpeaker | AVFoundation.AVAudioSessionCategoryOptions.MixWithOthers | AVFoundation.AVAudioSessionCategoryOptions.AllowBluetoothA2DP);
            session.SetActive(true);
        }
        catch (Exception) { }
#endif
        _host = new TimerHost(audio) { SettingsPath = SettingsPath };
        if (File.Exists(SettingsPath))
        {
            try { foreach (string p in _host.Load(SettingsPath)) _host.Log("settings: " + p); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { _host.Log("settings not loaded: " + ex.Message); }
        }
        _host.Start();

        // autosave a moment after changes
        _saveTimer = new System.Timers.Timer(1500) { AutoReset = false };
        _saveTimer.Elapsed += (_, _) => SaveNow();
        _host.StatusChanged += () => { _saveTimer.Stop(); _saveTimer.Start(); };
    }

    public static void SaveNow()
    {
        try { _host?.Save(SettingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _host?.Log("save failed: " + ex.Message); }
    }

    /// <summary>Asks for microphone access where needed; true when granted.</summary>
    public static async Task<bool> EnsureMicrophoneAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
        if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<Permissions.Microphone>();
        return status == PermissionStatus.Granted;
    }
}
