using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class StoragePresenterTests
{
    // Decimals are written in the user's locale; the expected texts here are for the invariant one.
    public StoragePresenterTests() => System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

    static StorageDrive Drive(bool removable = false, string note = "", params StorageVolume[] volumes) =>
        new("d0", "SERIAL", "Samsung 990", 0, "NVMe", 2_000_000_000_000, removable, volumes) { IdentityNote = note };
    // Letters are kept without the colon, as WindowsStorage stores them.
    static readonly StorageVolume C = new("C", "System", 1_000_000_000_000, 250_000_000_000), D = new("D", "Data", 500_000_000_000, 500_000_000_000);
    static StorageHealth Health(string badge = "Healthy", double? temperature = 41.5, double? life = 97, double? hours = 1234.6, double? max = 60, string? unavailable = null) =>
        new("d0", badge, temperature, life, null, hours, max, unavailable);

    [Fact] public void The_title_gives_the_name_bus_and_size_and_notes_identity_and_removable()
    {
        Assert.Equal("Samsung 990 · NVMe · 2.0 TB", StoragePresenter.Title(Drive()));
        Assert.Equal("C: Samsung 990 · NVMe · 2.0 TB", StoragePresenter.Title(Drive(volumes: [C])));
        Assert.Equal("C: D: Samsung 990 · NVMe · 2.0 TB · no serial number · removable", StoragePresenter.Title(Drive(removable: true, note: "no serial number", volumes: [C, D])));
    }

    [Fact] public void A_drive_without_volumes_is_named_by_its_model() =>
        Assert.Equal("Samsung 990", StoragePresenter.Name(Drive()));

    [Fact] public void A_drive_with_one_volume_is_named_by_its_letter_then_its_model() =>
        Assert.Equal("C: Samsung 990", StoragePresenter.Name(Drive(volumes: [C])));

    [Fact] public void A_drive_with_several_volumes_gives_every_letter_in_order_before_its_model() =>
        Assert.Equal("D: E: WD Game Drive", StoragePresenter.Name(Drive(volumes: [D, new("E", "Games", 1, 1)]) with { Model = "WD Game Drive" }));

    [Fact] public void A_volume_with_a_blank_letter_is_left_out_of_the_name()
    {
        Assert.Equal("C: Samsung 990", StoragePresenter.Name(Drive(volumes: [new("", "Recovery", 1, 1), C, new(" ", "", 1, 1)])));
        Assert.Equal("Samsung 990", StoragePresenter.Name(Drive(volumes: [new("", "Recovery", 1, 1)])));
    }

    [Fact] public void The_health_line_gives_temperature_badge_and_life_with_dashes_for_unknowns()
    {
        Assert.Equal("41.5°C · Healthy · Life 97%", StoragePresenter.HealthLine(Health()));
        Assert.Equal("— · — · Life —", StoragePresenter.HealthLine(null));
        Assert.Equal("— · — · Life —", StoragePresenter.HealthLine(Health(badge: "", temperature: null, life: null)));
    }

    [Fact] public void A_failing_drive_is_flagged()
    {
        Assert.True(StoragePresenter.Failing(Health(badge: "Failing")));
        Assert.False(StoragePresenter.Failing(Health()));
        Assert.False(StoragePresenter.Failing(null));
    }

    [Fact] public void Free_space_sums_the_volumes_and_gives_the_used_fraction()
    {
        Assert.Equal(("750.0 GB free of 1.5 TB", 0.5), StoragePresenter.Free(Drive(volumes: [C, D])));
        Assert.Equal(("No volumes", 0d), StoragePresenter.Free(Drive()));
    }

    [Fact] public void Volumes_list_letter_label_used_and_free()
    {
        var rows = StoragePresenter.Volumes(Drive(volumes: [C, D]));
        Assert.Equal(["C", "System", "750.0 GB", "250.0 GB"], rows[0].Cells);
        Assert.Equal(["D", "Data", "0.0 B", "500.0 GB"], rows[1].Cells);
        Assert.Empty(StoragePresenter.Volumes(Drive()));
    }

    [Fact] public void Details_give_hours_highest_temperature_life_and_any_notes_not_already_in_the_status()
    {
        Assert.Equal(["Power-on hours · 1235", "Highest temperature · 60.0°C", "Remaining life · 97%"], StoragePresenter.Details(Drive(), Health(), status: ""));
        Assert.Equal(["Power-on hours · —", "Highest temperature · —", "Remaining life · —"], StoragePresenter.Details(Drive(), null, status: ""));
        var noted = StoragePresenter.Details(Drive(note: "no serial number"), Health(unavailable: "SMART needs administrator"), status: "");
        Assert.Equal(["SMART needs administrator", "Identity · no serial number · live only, not kept in history"], noted.Skip(3));
        Assert.DoesNotContain("SMART needs administrator", StoragePresenter.Details(Drive(), Health(unavailable: "SMART needs administrator"), status: "SMART needs administrator"));
    }

    [Fact] public void The_status_prefers_the_storage_status_then_health_notes_then_storage_parts_of_the_hardware_status()
    {
        Assert.Equal("Scanning", StoragePresenter.Status("Scanning", [Health(unavailable: "x")], "Disk probe failed"));
        Assert.Equal("a · b", StoragePresenter.Status("", [Health(unavailable: "a"), Health(unavailable: "b"), Health(unavailable: "a"), Health()], "Disk probe failed"));
        Assert.Equal("Drive health limited · Storage sensors off", StoragePresenter.Status(" ", [Health()], "GPU ok · Drive health limited · Fans ok · Storage sensors off"));
        Assert.Equal("", StoragePresenter.Status("", [], null));
    }
}
