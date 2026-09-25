using Kairo.Core.Agent;
using Kairo.Core.AI.Jev;
using Kairo.Core.Models;
using Kairo.Core.Perception;
using Kairo.Core.Telemetry;
using Kairo.Core.Tests.Fakes;

namespace Kairo.Core.Tests;

public class TargetResolverTests
{
    private static async Task<UiSnapshot> Snapshot(FakeDesktop desktop) =>
        (await desktop.CaptureAsync(desktop.Window, new(), CancellationToken.None))!;

    private static AgentAction Fill(int? target, string label, string value) =>
        new() { Kind = ActionKind.SetValue, TargetId = target, TargetLabel = label, Value = value, Description = $"{label} eintragen" };

    [Fact]
    public async Task All_steps_are_resolved_with_a_single_jev_request_containing_only_valid_actions()
    {
        var desktop = FakeDesktop.ContactForm();
        var snapshot = await Snapshot(desktop);
        var jev = new FakeJev();
        var resolver = new TargetResolver(jev, KairoLogger.Null);

        var steps = new[]
        {
            Fill(1, "Vorname", "Max"),
            Fill(2, "Nachname", "Muster"),
            Fill(3, "E-Mail", "max@example.ch"),
            Fill(4, "Telefon", "+41 79 123 45 67"),
            new AgentAction { Kind = ActionKind.SetChecked, TargetId = 7, TargetLabel = "Ich akzeptiere die Datenschutzerklärung", Checked = true, Description = "Datenschutz" },
        };

        var resolved = await resolver.ResolveAsync(steps, snapshot, "Fülle das Formular aus", "~typesafe/jev-latest", true, 0.7, null, CancellationToken.None);

        var request = Assert.Single(jev.Requests);
        Assert.Equal("resolve", request.Purpose);
        Assert.Equal("~typesafe/jev-latest", request.Model);
        // The checkbox is the only toggleable element → resolved locally without a question.
        Assert.Equal(4, request.Questions.Count);
        foreach (var q in request.Questions.Values)
        {
            Assert.Equal(JevQuestionType.Choice, q.Type);
            Assert.Contains(TargetResolver.NoneKey, q.Options!.Keys);
            // Only edit fields are offered for text input – never buttons, links or checkboxes.
            Assert.DoesNotContain("e8", q.Options.Keys);
            Assert.DoesNotContain("e9", q.Options.Keys);
            Assert.DoesNotContain("e7", q.Options.Keys);
        }

        Assert.All(resolved, r => Assert.True(r.IsResolved));
        Assert.Equal([1, 2, 3, 4, 7], resolved.Select(r => r.Element!.Id).ToArray());
        Assert.Equal(ResolutionSource.Unambiguous, resolved[4].Source);
        Assert.Equal(ResolutionSource.JevAgreesWithPlanner, resolved[0].Source);
    }

    [Fact]
    public async Task Jev_overrides_a_wrong_planner_id_when_confident()
    {
        var desktop = FakeDesktop.ContactForm();
        var snapshot = await Snapshot(desktop);
        var jev = new FakeJev();
        var resolver = new TargetResolver(jev, KairoLogger.Null);

        // Planner pointed at [4] Telefon for the e-mail address.
        var resolved = await resolver.ResolveAsync([Fill(4, "E-Mail", "max@example.ch")], snapshot, "x", "m", true, 0.7, null, CancellationToken.None);
        Assert.Equal(3, resolved[0].Element!.Id);
        Assert.Contains(resolved[0].Source, new[] { ResolutionSource.JevOverride, ResolutionSource.JevChoice });
    }

    [Fact]
    public async Task Without_planner_id_jev_decides_and_none_means_unresolved()
    {
        var desktop = FakeDesktop.ContactForm();
        var snapshot = await Snapshot(desktop);
        var jev = new FakeJev();
        var resolver = new TargetResolver(jev, KairoLogger.Null);

        var resolved = await resolver.ResolveAsync(
            [Fill(null, "Nachname", "Muster"), Fill(null, "Steuernummer", "123")],
            snapshot, "x", "m", true, 0.7, null, CancellationToken.None);

        Assert.Equal(2, resolved[0].Element!.Id);
        Assert.Equal(ResolutionSource.JevChoice, resolved[0].Source);
        Assert.False(resolved[1].IsResolved);
        Assert.NotNull(resolved[1].Problem);
    }

    [Fact]
    public async Task Falls_back_to_planner_hint_when_jev_is_down()
    {
        var desktop = FakeDesktop.ContactForm();
        var snapshot = await Snapshot(desktop);
        var resolver = new TargetResolver(new FakeJev { Fail = true }, KairoLogger.Null);
        var resolved = await resolver.ResolveAsync([Fill(2, "Nachname", "Muster"), Fill(null, "E-Mail", "a@b.ch")], snapshot, "x", "m", true, 0.7, null, CancellationToken.None);
        Assert.Equal(ResolutionSource.PlannerFallback, resolved[0].Source);
        Assert.Equal(2, resolved[0].Element!.Id);
        Assert.Equal(ResolutionSource.LocalMatch, resolved[1].Source);
        Assert.Equal(3, resolved[1].Element!.Id);
    }

    [Fact]
    public async Task Two_steps_never_write_into_the_same_field()
    {
        var desktop = FakeDesktop.ContactForm();
        var snapshot = await Snapshot(desktop);
        var jev = new FakeJev();
        jev.ForcedChoices["Name"] = "e2";
        var resolver = new TargetResolver(jev, KairoLogger.Null);
        var resolved = await resolver.ResolveAsync([Fill(2, "Nachname", "Muster"), Fill(2, "Name", "Muster")], snapshot, "x", "m", true, 0.7, null, CancellationToken.None);
        Assert.Single(resolved, r => r.IsResolved);
        Assert.Single(resolved, r => r.Source == ResolutionSource.Unresolved);
    }

    [Fact]
    public async Task Offscreen_targets_lead_to_scroll_option()
    {
        var desktop = FakeDesktop.ContactForm();
        desktop.Fields.RemoveAll(f => f.Name == "Telefon");
        var snapshot = await Snapshot(desktop) with { Truncated = true };
        var jev = new FakeJev();
        jev.ForcedChoices["Telefon"] = TargetResolver.ScrollKey;
        var resolver = new TargetResolver(jev, KairoLogger.Null);
        var resolved = await resolver.ResolveAsync([Fill(null, "Telefon", "+41 79 000 00 00")], snapshot, "x", "m", true, 0.7, null, CancellationToken.None);
        Assert.Equal(ResolutionSource.NeedsScroll, resolved[0].Source);
        Assert.Contains(TargetResolver.ScrollKey, jev.Requests.Single().Questions.Values.Single().Options!.Keys);
    }

    [Fact]
    public void Remap_finds_same_element_in_new_snapshot()
    {
        var old = new UiElement { Id = 3, Role = ElementRole.Edit, Name = "E-Mail", Locator = "a", Capabilities = ElementCapabilities.SetValue };
        var snapshot = new UiSnapshot
        {
            SnapshotId = "n",
            Source = PerceptionSource.UiAutomation,
            Window = new WindowInfo { Handle = 1, Title = "", ProcessName = "" },
            Elements =
            [
                new UiElement { Id = 10, Role = ElementRole.Edit, Name = "Name", Locator = "b", Capabilities = ElementCapabilities.SetValue },
                new UiElement { Id = 11, Role = ElementRole.Edit, Name = "E-Mail", Locator = "c", Capabilities = ElementCapabilities.SetValue },
            ],
        };
        var remapped = TargetResolver.Remap(old, Fill(3, "E-Mail", "x"), snapshot);
        Assert.Equal(11, remapped!.Id);
    }

    [Theory]
    [InlineData("E-Mail", "Email address", 0.7)]
    [InlineData("Telefon", "Phone", 0.7)]
    [InlineData("Vorname", "First name", 0.7)]
    [InlineData("PLZ", "Postleitzahl", 0.7)]
    public void Matcher_knows_common_german_english_synonyms(string wanted, string name, double min)
    {
        var e = new UiElement { Id = 1, Role = ElementRole.Edit, Name = name, Locator = "x", Capabilities = ElementCapabilities.SetValue };
        Assert.True(ElementMatcher.Similarity(wanted, e) >= min);
    }
}
