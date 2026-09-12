using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using TicketMiser.Desk.Primitives;
using TicketMiser.Desk.Theming;

namespace TicketMiser.Desk.Tests;

/// <summary>
/// The accent picker: one swatch per hue with radiogroup semantics, the chosen one ringed.
/// It follows the gate's keyboard contract so an operator who learnt one control has
/// learnt the other.
/// </summary>
public class DeskAccentPickerTests : DeskTestContext
{
    private IRenderedComponent<DeskAccentPicker> Picker(
        DeskAccent value,
        EventCallback<DeskAccent> changed = default,
        bool isDark = true)
        => RenderComponent<DeskAccentPicker>(p => p
            .Add(x => x.Value, value)
            .Add(x => x.IsDark, isDark)
            .Add(x => x.ValueChanged, changed));

    [Fact]
    public void Renders_a_radiogroup_with_one_swatch_per_accent()
    {
        var cut = Picker(DeskAccent.Blue);

        Assert.Equal("Accent", cut.Find("[role=radiogroup]").GetAttribute("aria-label"));
        Assert.Equal(DeskAccents.All.Count, cut.FindAll("[role=radio]").Count);
    }

    /// <summary>
    /// A swatch is a coloured circle with no words on it, so its name has to be carried
    /// as an attribute or a screen reader meets five unlabelled buttons.
    /// </summary>
    [Fact]
    public void Every_swatch_is_named_and_painted_in_its_own_hue()
    {
        var radios = Picker(DeskAccent.Blue).FindAll("[role=radio]");

        for (var i = 0; i < radios.Count; i++)
        {
            var accent = DeskAccents.All[i];

            Assert.Equal(DeskAccents.Label(accent), radios[i].GetAttribute("aria-label"));
            Assert.Contains(DeskAccents.Dark(accent).Accent, radios[i].GetAttribute("style"));
            Assert.Equal("button", radios[i].GetAttribute("type"));
        }
    }

    [Fact]
    public void Swatches_are_drawn_in_the_light_values_on_the_light_desk()
    {
        var purple = Picker(DeskAccent.Blue, isDark: false)
            .Find($"[data-accent={DeskAccents.Key(DeskAccent.Purple)}]");

        Assert.Contains(DeskAccents.Light(DeskAccent.Purple).Accent, purple.GetAttribute("style"));
    }

    [Fact]
    public void Only_the_chosen_swatch_is_checked_ringed_and_in_the_tab_order()
    {
        var radios = Picker(DeskAccent.Indigo).FindAll("[role=radio]");

        var chosen = radios.Single(r => r.GetAttribute("aria-checked") == "true");

        Assert.Equal("indigo", chosen.GetAttribute("data-accent"));
        Assert.Contains("swatch--on", chosen.GetAttribute("class"));
        Assert.Equal("0", chosen.GetAttribute("tabindex"));
        Assert.All(radios.Where(r => r != chosen), r => Assert.Equal("-1", r.GetAttribute("tabindex")));
    }

    [Fact]
    public void Clicking_a_swatch_raises_its_accent()
    {
        DeskAccent? chosen = null;
        var cut = Picker(DeskAccent.Blue, EventCallback.Factory.Create<DeskAccent>(this, a => chosen = a));

        cut.Find("[data-accent=teal]").Click();

        Assert.Equal(DeskAccent.Teal, chosen);
    }

    [Fact]
    public void Clicking_the_chosen_swatch_raises_nothing()
    {
        var raised = 0;
        var cut = Picker(DeskAccent.Blue, EventCallback.Factory.Create<DeskAccent>(this, _ => raised++));

        cut.Find("[data-accent=blue]").Click();

        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData("ArrowRight", DeskAccent.Teal)]
    [InlineData("ArrowLeft", DeskAccent.Graphite)]
    [InlineData("End", DeskAccent.Graphite)]
    public void Arrows_move_the_choice_and_wrap(string key, DeskAccent expected)
    {
        DeskAccent? chosen = null;
        var cut = Picker(DeskAccent.Blue, EventCallback.Factory.Create<DeskAccent>(this, a => chosen = a));

        cut.Find("[role=radiogroup]").KeyDown(new KeyboardEventArgs { Key = key });

        Assert.Equal(expected, chosen);
    }
}
