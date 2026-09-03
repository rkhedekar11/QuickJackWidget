// UseWindowsForms adds `global using System.Windows.Forms`, which collides with WPF on
// several very common type names. These aliases pick the WPF side everywhere; the few
// places that genuinely want the WinForms type (the tray icon, Screen) name it explicitly.
global using Application = System.Windows.Application;
global using Clipboard = System.Windows.Clipboard;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using MessageBox = System.Windows.MessageBox;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
global using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
