using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class KeyPressTableTests
{
    // Every name DeadLight's keyboard-input.ts knows, with the Windows set-1 scan code and extended flag it must give (the brief's table).
    private static readonly (string Names,int Scan,bool Ext)[] Groups=
    {
        ("A",0x1E,false),("B",0x30,false),("C",0x2E,false),("D",0x20,false),("E",0x12,false),("F",0x21,false),("G",0x22,false),("H",0x23,false),("I",0x17,false),
        ("J",0x24,false),("K",0x25,false),("L",0x26,false),("M",0x32,false),("N",0x31,false),("O",0x18,false),("P",0x19,false),("Q",0x10,false),("R",0x13,false),
        ("S",0x1F,false),("T",0x14,false),("U",0x16,false),("V",0x2F,false),("W",0x11,false),("X",0x2D,false),("Y",0x15,false),("Z",0x2C,false),
        ("1",0x02,false),("2",0x03,false),("3",0x04,false),("4",0x05,false),("5",0x06,false),("6",0x07,false),("7",0x08,false),("8",0x09,false),("9",0x0A,false),("0",0x0B,false),
        ("F1",0x3B,false),("F2",0x3C,false),("F3",0x3D,false),("F4",0x3E,false),("F5",0x3F,false),("F6",0x40,false),("F7",0x41,false),("F8",0x42,false),("F9",0x43,false),("F10",0x44,false),
        ("F11",0x57,false),("F12",0x58,false),
        ("Escape|Esc",0x01,false),("Backquote|Grave|`",0x29,false),("Minus|-",0x0C,false),("Equals|=",0x0D,false),("Backspace",0x0E,false),("Tab",0x0F,false),
        ("Caps Lock|CapsLock",0x3A,false),("Enter|Return",0x1C,false),("Left Bracket|[",0x1A,false),("Right Bracket|]",0x1B,false),
        ("Backslash|\\|\\ (ANSI)",0x2B,false),("\\ (ISO)",0x56,false),("Semicolon|;",0x27,false),("Apostrophe|Quote|'",0x28,false),("Comma|,",0x33,false),("Period|.",0x34,false),
        ("Slash|/",0x35,false),("Space|Spacebar",0x39,false),("Left Shift",0x2A,false),("Right Shift",0x36,false),("Left Control|Left Ctrl",0x1D,false),("Right Control|Right Ctrl",0x1D,true),
        ("Left Alt",0x38,false),("Right Alt",0x38,true),("Left Windows|Left Win|Left Meta",0x5B,true),("Right Windows|Right Win|Right Meta",0x5C,true),("Menu|Application",0x5D,true),
        ("Print Screen",0x37,true),("Scroll Lock",0x46,false),("Pause|Pause Break|Pause/Break",0x45,false),
        ("Insert",0x52,true),("Delete",0x53,true),("Home",0x47,true),("End",0x4F,true),("Page Up",0x49,true),("Page Down",0x51,true),
        ("Up|Up Arrow",0x48,true),("Left|Left Arrow",0x4B,true),("Right|Right Arrow",0x4D,true),("Down|Down Arrow",0x50,true),
        ("Num Lock",0x45,true),("Num /|Numpad Divide|Number Pad /",0x35,true),("Num *|Numpad Multiply|Number Pad *",0x37,false),
        ("Num -|Numpad Subtract|Number Pad -",0x4A,false),("Num +|Numpad Add|Number Pad +",0x4E,false),("Num Enter|Numpad Enter|Number Pad Enter",0x1C,true),
        ("Num 0|Numpad 0|Number Pad 0",0x52,false),("Num 1|Numpad 1|Number Pad 1",0x4F,false),("Num 2|Numpad 2|Number Pad 2",0x50,false),("Num 3|Numpad 3|Number Pad 3",0x51,false),
        ("Num 4|Numpad 4|Number Pad 4",0x4B,false),("Num 5|Numpad 5|Number Pad 5",0x4C,false),("Num 6|Numpad 6|Number Pad 6",0x4D,false),("Num 7|Numpad 7|Number Pad 7",0x47,false),
        ("Num 8|Numpad 8|Number Pad 8",0x48,false),("Num 9|Numpad 9|Number Pad 9",0x49,false),("Num .|Numpad Decimal|Number Pad .",0x53,false),
        ("#",0x2B,false),
    };

    public static IEnumerable<object[]> AllNames()
    {
        foreach(var (names,scan,ext) in Groups)
            foreach(var n in names.Split('|')) yield return new object[]{n,scan,ext};
    }

    [Theory,MemberData(nameof(AllNames))]
    public void Every_DeadLight_name_maps(string name,int scan,bool ext)
    {
        Assert.Equal((scan,ext),KeyPressTable.CodeFor(name));
        Assert.Equal((scan,ext),KeyPressTable.CodeFor("Key: "+name));
    }

    [Fact]
    public void Names_match_with_and_without_the_prefix_and_any_case()
    {
        Assert.Equal((0x1D,true),KeyPressTable.CodeFor("Key: Right Control"));
        Assert.Equal((0x1D,true),KeyPressTable.CodeFor("  RIGHT control "));
        Assert.Equal((0x1D,true),KeyPressTable.CodeFor("key:right ctrl"));
        Assert.Equal((0x1D,true),KeyPressTable.CodeFor("KEY:   Right Ctrl  "));
        Assert.Equal("right ctrl",KeyPressTable.Normalize(" Key: Right Ctrl "));
        Assert.Null(KeyPressTable.CodeFor(""));
        Assert.Null(KeyPressTable.CodeFor("Key:"));
    }

    [Fact]
    public void Number_pad_digits_are_apart_from_the_number_row()
    {
        Assert.Equal((0x02,false),KeyPressTable.CodeFor("1"));
        Assert.Equal((0x4F,false),KeyPressTable.CodeFor("Key: Number Pad 1"));
        var names=new[]{"Key: 1","Key: Number Pad 1"};
        Assert.Equal(new[]{0},KeyPressTable.LightsFor(names,0x02,false));
        Assert.Equal(new[]{1},KeyPressTable.LightsFor(names,0x4F,false));
        Assert.Empty(KeyPressTable.LightsFor(names,0x4F,true));
    }

    [Fact]
    public void Number_pad_Enter_is_apart_from_Enter()
    {
        var names=new[]{"Key: Enter","Key: Number Pad Enter"};
        Assert.Equal(new[]{0},KeyPressTable.LightsFor(names,0x1C,false));
        Assert.Equal(new[]{1},KeyPressTable.LightsFor(names,0x1C,true));
    }

    [Fact]
    public void The_ISO_and_ANSI_backslashes_are_apart()
    {
        var names=new[]{"Key: \\ (ANSI)","Key: \\ (ISO)"};
        Assert.Equal(new[]{0},KeyPressTable.LightsFor(names,0x2B,false));
        Assert.Equal(new[]{1},KeyPressTable.LightsFor(names,0x56,false));
    }

    [Fact]
    public void Pause_and_Num_Lock_are_apart()
    {
        var names=new[]{"Key: Pause/Break","Key: Num Lock"};
        Assert.Equal(new[]{0},KeyPressTable.LightsFor(names,0x45,false));
        Assert.Equal(new[]{1},KeyPressTable.LightsFor(names,0x45,true));
    }

    [Fact]
    public void Right_Shift_matches_with_either_flag()
    {
        var names=new[]{"Key: Left Shift","Key: Right Shift"};
        Assert.Equal(new[]{1},KeyPressTable.LightsFor(names,0x36,false));
        Assert.Equal(new[]{1},KeyPressTable.LightsFor(names,0x36,true));
        Assert.Equal(new[]{0},KeyPressTable.LightsFor(names,0x2A,false));
    }

    [Theory]
    [InlineData("Key: Fn")]
    [InlineData("Key: Japanese Yen")]
    [InlineData("Key: OEM 102")]
    [InlineData("Key: _")]
    [InlineData("Logo")]
    [InlineData("Key: M1")]
    public void Unmapped_names_map_to_nothing(string name)
    {
        Assert.Null(KeyPressTable.CodeFor(name));
        Assert.Empty(KeyPressTable.LightsFor(new[]{name},0x1E,false));
    }

    [Fact]
    public void LightsFor_returns_every_light_with_that_name()
    {
        var names=new[]{"Key: A","Key: B","Key: A","Logo","a"};
        Assert.Equal(new[]{0,2,4},KeyPressTable.LightsFor(names,0x1E,false));
        Assert.Empty(KeyPressTable.LightsFor(names,0x1E,true));
        Assert.Empty(KeyPressTable.LightsFor(names,0x7F,false));
    }

    [Fact]
    public void Unrecognised_lists_the_names_that_map_to_nothing()
    {
        Assert.Equal(new[]{"Key: Fn","Logo"},KeyPressTable.Unrecognised(new[]{"Key: A","Key: Fn","Key: Right Shift","Logo"}));
        Assert.Empty(KeyPressTable.Unrecognised(new[]{"Key: A"}));
        Assert.Empty(KeyPressTable.Unrecognised(Array.Empty<string>()));
    }
}
