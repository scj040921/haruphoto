using Windows.UI.ViewManagement;

namespace PhotoAlbum;

public sealed partial class MainWindow
{
    private readonly UISettings _motionPreferences = new();
    private bool MotionEnabled => _settings.AnimationsEnabled && _motionPreferences.AnimationsEnabled;
}
