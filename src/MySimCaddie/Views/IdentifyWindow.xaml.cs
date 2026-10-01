using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MySimCaddie.Core.Displays;
using MySimCaddie.Services;

namespace MySimCaddie.Views;

/// <summary>Big "this is display #2 — Projector" card shown on each display for a few seconds.</summary>
public partial class IdentifyWindow : Window
{
    private readonly DisplayInfo _display;

    public IdentifyWindow(DisplayInfo display, string role)
    {
        InitializeComponent();
        _display = display;

        NumberText.Text = display.Number.ToString();
        RoleText.Text = string.IsNullOrWhiteSpace(role) ? "Not assigned" : role;
        DetailText.Text = display.Description;

        SourceInitialized += (_, _) => WpfWindowTools.CoverDisplay(this, _display);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e) => Close();
}
