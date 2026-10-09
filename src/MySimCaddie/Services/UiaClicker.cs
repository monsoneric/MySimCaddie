using System.Windows.Automation;
using MySimCaddie.Core;
using MySimCaddie.Core.Platform;

namespace MySimCaddie.Services;

/// <summary>UI Automation fallback for <see cref="AutoClicker"/>: works with buttons that aren't classic Win32 controls.</summary>
public static class UiaClicker
{
    public static bool Click(IntPtr window, string buttonText)
    {
        var want = AutoClicker.Normalize(buttonText);
        var root = AutomationElement.FromHandle(window);
        var buttons = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        foreach (AutomationElement b in buttons)
        {
            if (AutoClicker.Normalize(b.Current.Name) != want || !b.Current.IsEnabled || b.Current.IsOffscreen) continue;

            if (b.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            {
                ((InvokePattern)pattern).Invoke();
                Log.Info($"Pressed \"{b.Current.Name}\" via UI Automation");
                return true;
            }
        }

        return false;
    }

    /// <summary>Selects a tab by its name, e.g. "Connection Manager" in GSPro Connect.</summary>
    public static bool SelectTab(IntPtr window, string tabName)
    {
        var root = AutomationElement.FromHandle(window);
        var want = AutoClicker.Normalize(tabName);
        var tabs = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

        foreach (AutomationElement t in tabs)
        {
            if (AutoClicker.Normalize(t.Current.Name) != want) continue;

            if (t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
            {
                ((SelectionItemPattern)sel).Select();
                Log.Info($"Selected the \"{t.Current.Name}\" tab via UI Automation");
                return true;
            }
        }

        return false;
    }
}
