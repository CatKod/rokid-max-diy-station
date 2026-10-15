// SPDX-License-Identifier: MIT
//
// Hidden WPF Window that hosts the H.NotifyIcon TaskbarIcon.  H.NotifyIcon
// requires the TaskbarIcon to live inside a WPF visual tree — if you only
// new up a TaskbarIcon and never put it anywhere, the Shell_TrayWnd
// registration never happens and the icon is invisible.
//
// This window is never shown to the user (ShowInTaskbar=false, Width=0,
// Height=0, WindowStyle=None) so it doesn't take a visible slot.  The
// MainWindow is the only window the user actually sees.
using System.Windows;

namespace RokidDiyStation.App;

public sealed class NotifyIconHost : Window
{
    public NotifyIconHost()
    {
        // Make the host invisible and out of the way.
        Width = 0;
        Height = 0;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        // Negative offset so it can't ever be accidentally dragged on-screen.
        Left = -32000;
        Top = -32000;
        // Show once so the window handle is created and the visual tree is
        // live, then hide.  The handle is what H.NotifyIcon needs to
        // register the TaskbarIcon with the Shell.
        Show();
    }
}
