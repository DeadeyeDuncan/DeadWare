using Microsoft.UI.Xaml;

namespace DeadWare.WinUI;

internal static class Lifetime
{
    /// <summary>
    /// Runs <paramref name="attach"/> when the element enters the live tree and <paramref name="detach"/> when it has left it.
    /// WinUI raises Unloaded after Loaded when an element is removed and added back in the same pass (a grid re-laying out
    /// its cards), so a plain Loaded / Unloaded pair ends up detached while on screen. A late Unloaded for an element that
    /// is loaded again is ignored, and a repeated Loaded does not attach twice.
    /// </summary>
    public static void WhileLoaded(this FrameworkElement element, Action attach, Action detach)
    {
        bool attached = false;
        element.Loaded += (_, _) =>
        {
            if (attached) return;
            attached = true;
            attach();
        };
        element.Unloaded += (_, _) =>
        {
            if (!attached || element.IsLoaded) return;
            attached = false;
            detach();
        };
    }
}
