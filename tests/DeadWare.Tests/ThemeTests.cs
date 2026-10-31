using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class ThemeTests
{
    [Fact] public void UnknownPersistedThemeFallsBackToReadableDefault()
    {Assert.Equal("Midnight",SettingsCodec.Read("{\"Theme\":\"missing\"}").Settings.Theme);}
    [Theory][InlineData("Midnight")][InlineData("Ember")][InlineData("Glacier")][InlineData("Amethyst")][InlineData("Neon")][InlineData("Blackout")][InlineData("Skyrim")][InlineData("Daylight")]
    public void PresetChoiceSurvivesSettingsRoundtrip(string theme)
    {Assert.Equal(theme,SettingsCodec.Read(SettingsCodec.Write(new UserSettings {Theme=theme})).Settings.Theme);}
}
