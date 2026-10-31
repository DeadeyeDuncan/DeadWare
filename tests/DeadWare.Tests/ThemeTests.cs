using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class ThemeTests
{
    [Fact] public void UnknownPersistedThemeFallsBackToReadableDefault()
    {Assert.Equal("Midnight",SettingsCodec.Read("{\"Theme\":\"missing\"}").Settings.Theme);}
    [Theory][InlineData("Midnight")][InlineData("Ember")][InlineData("Glacier")][InlineData("Amethyst")][InlineData("Neon")][InlineData("Blackout")][InlineData("Skyrim")][InlineData("Mage")][InlineData("Daylight")]
    public void PresetChoiceSurvivesSettingsRoundtrip(string theme)
    {Assert.Equal(theme,SettingsCodec.Read(SettingsCodec.Write(new UserSettings {Theme=theme})).Settings.Theme);}
    [Theory][InlineData("Off")][InlineData("Subtle")][InlineData("Balanced")][InlineData("Strong")]
    public void GlassLevelSurvivesSettingsRoundtrip(string level)
    {Assert.Equal(level,SettingsCodec.Read(SettingsCodec.Write(new UserSettings {GlassLevel=level})).Settings.GlassLevel);}
    [Theory][InlineData("{}")][InlineData("{\"GlassLevel\":\"missing\"}")][InlineData("{\"GlassLevel\":null}")][InlineData("{\"GlassLevel\":\"strong\"}")]
    public void MissingOrUnknownGlassLevelUsesBalanced(string json)
    {Assert.Equal("Balanced",SettingsCodec.Read(json).Settings.GlassLevel);}
}
