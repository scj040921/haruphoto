# Interaction components

Haruphoto uses WinUI 3 on .NET 8. CommunityToolkit.WinUI.Controls.Segmented 8.2.251219 is pinned in PhotoAlbum.csproj. NuGet restore installs its matching Extensions and Common dependencies. No JavaScript or web runtime is needed.

Segmented is used for the gallery layout and filmstrip preferences. These controls edit the existing settings draft; Save, Cancel and per-section reset retain the same behavior.

MotionEnabled combines the application setting with Windows UISettings.AnimationsEnabled. Map transitions use Composition scale, translation and opacity; thumbnail decoding must not block location entrance. Repeated return requests share one active transition.

Build with .NET 8 SDK, Release, win-x64 and --self-contained true. WindowsAppSDKSelfContained stays enabled. The existing GitHub release workflow restores dependencies and bundles .NET, Windows App SDK and VC++ runtime files in both installer and portable assets.

Sources: https://github.com/CommunityToolkit/Windows and https://learn.microsoft.com/en-us/dotnet/communitytoolkit/windows/segmented/
