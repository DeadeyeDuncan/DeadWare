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
    public static void WhileLoaded(this FrameworkElement element, Action attach, Action detach) => Track(element, attach, detach, false);

    /// <summary>
    /// As <see cref="WhileLoaded"/>, and also detached while the element's window is hidden or minimized, so a dashboard in
    /// the tray redraws nothing per reading. For elements whose attach only subscribes and refreshes: attaching again on
    /// return is what brings them up to date.
    /// </summary>
    public static void WhileShowing(this FrameworkElement element, Action attach, Action detach) => Track(element, attach, detach, true);

    private static void Track(FrameworkElement element, Action attach, Action detach, bool whileHostVisible)
    {
        bool attached = false;
        XamlRoot? watched = null;
        void Update()
        {
            bool wanted = element.IsLoaded && (watched?.IsHostVisible ?? true);
            if (wanted == attached) return;
            attached = wanted;
            if (wanted) attach(); else detach();
        }
        void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Update();
        void Watch(XamlRoot? root)
        {
            if (ReferenceEquals(root, watched)) return;
            if (watched is not null) watched.Changed -= RootChanged;
            watched = root;
            if (watched is not null) watched.Changed += RootChanged;
        }
        element.Loaded += (_, _) => { if (whileHostVisible) Watch(element.XamlRoot); Update(); };
        element.Unloaded += (_, _) =>
        {
            if (element.IsLoaded) return;
            Watch(null);
            Update();
        };
    }
}
