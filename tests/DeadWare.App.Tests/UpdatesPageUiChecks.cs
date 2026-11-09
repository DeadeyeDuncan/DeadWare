using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>0.17.11: Updates keeps the version lines, actions, progress and statuses in one controls card, then the release notes, then a folded explanation, at a narrow and a wide page.</summary>
internal static class UpdatesPageUiChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    internal static void Run(UiSession session)
    {
        var page=new UpdatesPage(session);
        T Field<T>(string name)=>(T)typeof(UpdatesPage).GetField(name,All)!.GetValue(page)!;
        TextBlock installed=Field<TextBlock>("installed"),status=Field<TextBlock>("status"),local=Field<TextBlock>("local");
        Button check=Field<Button>("check"),history=Field<Button>("history");var progress=Field<ProgressBar>("progress");
        var failed=new UpdateState("0.17.11"){Status="Update could not continue: fixture failure",LocalStatus="Rollback available: 0.17.10."};
        foreach(var size in new[]{new Size(620,720),new Size(1400,900)})
        {
            void Apply(UpdateState state){page.ApplyState(state);page.Measure(size);page.Arrange(new Rect(size));page.UpdateLayout();}
            Rect Bounds(FrameworkElement element)=>element.TransformToAncestor(page).TransformBounds(new Rect(element.RenderSize));
            Apply(failed);
            // Three cards in reading order: controls, release notes, folded explanation.
            var body=Assert.IsType<StackPanel>(page.Content);
            var cards=body.Children.OfType<Border>().ToArray();Assert.Equal(3,cards.Length);Assert.Equal(3,body.Children.Count);
            Border controls=cards[0],notes=cards[1],about=cards[2];
            foreach(FrameworkElement element in new FrameworkElement[]{installed,check,progress,status,local})Assert.True(element.IsDescendantOf(controls),element.GetType().Name+" sits in the controls card");
            Assert.True(history.IsDescendantOf(notes));
            // A failure and the installation status read under the buttons and above the release notes.
            var shown=Shown(page);
            Assert.Contains(status,shown);Assert.Contains(local,shown);Assert.DoesNotContain(progress,shown);
            Assert.True(Bounds(check).Bottom<=Bounds(status).Top&&Bounds(status).Bottom<=Bounds(local).Top&&Bounds(local).Bottom<=Bounds(notes).Top,$"{size.Width}: check {Bounds(check)}, status {Bounds(status)}, local {Bounds(local)}, notes {Bounds(notes)}");
            Assert.True(Bounds(notes).Bottom<=Bounds(about).Top);
            // Busy: progress shows in the same card, between the buttons and the status.
            Apply(failed with {Busy=true,Progress=40,Status="Downloading 0.17.12 · 40%"});
            Assert.Contains(progress,Shown(page));
            Assert.True(Bounds(check).Bottom<=Bounds(progress).Top&&Bounds(progress).Bottom<=Bounds(status).Top,$"{size.Width}: progress {Bounds(progress)}");
            // Nothing to say: the status lines take no room.
            Apply(new UpdateState("0.17.11"){Status=""});
            Assert.DoesNotContain(status,Shown(page));Assert.DoesNotContain(local,Shown(page));
            // The explanation is folded until opened.
            var expander=Assert.IsType<Expander>(about.Child);Assert.Equal("How updates and rollback work",expander.Header);Assert.False(expander.IsExpanded);
            bool Explained()=>Shown(page).OfType<TextBlock>().Any(text=>text.Text.StartsWith("DeadWare checks normal GitHub releases once at startup.",StringComparison.Ordinal));
            Assert.False(Explained());
            double folded=Bounds(about).Height;Assert.True(folded<60,$"{size.Width}: folded explanation card is {folded} high");
            expander.IsExpanded=true;Apply(failed);
            Assert.True(Explained());Assert.True(Bounds(about).Height>folded);
            Assert.Contains(Shown(page).OfType<TextBlock>(),text=>text.Text.StartsWith("The first updater-enabled installation",StringComparison.Ordinal));
            expander.IsExpanded=false;Apply(failed);Assert.False(Explained());
        }
    }
    /// <summary>Descendants that are shown: a subtree whose root is Hidden or Collapsed is skipped.</summary>
    private static List<DependencyObject> Shown(DependencyObject root)
    {
        var shown=new List<DependencyObject>();
        void Walk(DependencyObject parent)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i);
                if(child is UIElement {Visibility:not Visibility.Visible})continue;
                shown.Add(child);Walk(child);
            }
        }
        Walk(root);return shown;
    }
}
