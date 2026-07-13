// This app enables both WPF and WinForms (WinForms only for the tray NotifyIcon),
// so a few type names exist in both System.Windows and System.Windows.Forms.
// Resolve the bare names to their WPF meaning app-wide; the tray code refers to
// WinForms types explicitly.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Keys = Pawse.Core.Keys;
