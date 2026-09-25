using Kairo.Core.AI;
using Kairo.Core.AI.Planning;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;
using Kairo.Core.Tests.Fakes;

namespace Kairo.Core.Tests;

public class PlanningTests
{
    [Fact]
    public void Parses_fenced_json_with_prose_and_maps_all_fields()
    {
        var content = """
            Here is the plan:
            ```json
            {"status":"Fülle Formular aus …","facts":[{"key":"email","value":"max@example.ch"}],
             "steps":[
               {"action":"set_value","target":3,"target_label":"E-Mail","value":"max@example.ch","description":"E-Mail eintragen"},
               {"action":"select_option","target":"5","target_label":"Land","option":"Schweiz","description":"Land wählen"},
               {"action":"set_checked","target":7,"target_label":"Datenschutz","checked":"true","description":"Datenschutz akzeptieren"},
               {"action":"hotkey","keys":"ctrl+s","description":"Speichern"},
               {"action":"teleport","description":"???"},
               {"action":"set_value","target":4,"target_label":"Telefon","description":"ohne Wert"}
             ],
             "after_steps":"verify_and_finish","final_message":"Formular ausgefüllt.","question":null}
            ```
            """;
        var plan = PlanParser.Parse(content);

        Assert.Equal("Fülle Formular aus …", plan.Status);
        Assert.Equal(4, plan.Steps.Count);
        Assert.Equal(2, plan.Warnings.Count);
        Assert.Equal(ActionKind.SetValue, plan.Steps[0].Kind);
        Assert.Equal(3, plan.Steps[0].TargetId);
        Assert.Equal(5, plan.Steps[1].TargetId);
        Assert.Equal("Schweiz", plan.Steps[1].Option);
        Assert.True(plan.Steps[2].Checked);
        Assert.Equal("ctrl+s", plan.Steps[3].Keys);
        Assert.Equal(PlanContinuation.VerifyAndFinish, plan.After);
        Assert.Equal("max@example.ch", plan.Facts.Single().Value);
        Assert.Equal("Formular ausgefüllt.", plan.FinalMessage);
    }

    [Fact]
    public void Informational_steps_force_replan()
    {
        var plan = PlanParser.Parse("""{"status":"Lese Datei","steps":[{"action":"read_file","path":"C:\\x.pdf","description":"lesen"}],"after_steps":"verify_and_finish"}""");
        Assert.Equal(PlanContinuation.Replan, plan.After);
    }

    [Fact]
    public void Ask_user_without_question_gets_default_question()
    {
        var plan = PlanParser.Parse("""{"status":"?","steps":[],"after_steps":"ask_user"}""");
        Assert.Equal(PlanContinuation.AskUser, plan.After);
        Assert.False(string.IsNullOrWhiteSpace(plan.Question));
    }

    [Fact]
    public void Invalid_json_throws_parse_exception()
    {
        Assert.Throws<PlanParseException>(() => PlanParser.Parse("I cannot do that."));
    }

    [Fact]
    public void Wire_names_roundtrip_for_all_actions()
    {
        foreach (var kind in Enum.GetValues<ActionKind>())
        {
            Assert.True(ActionKindExtensions.TryParseWireName(kind.ToWireName(), out var parsed));
            Assert.Equal(kind, parsed);
        }
        Assert.True(ActionKindExtensions.TryParseWireName("Set-Value", out var k));
        Assert.Equal(ActionKind.SetValue, k);
    }

    [Fact]
    public void Schema_lists_all_actions_and_is_non_strict()
    {
        var schema = PlannerPrompt.Schema;
        Assert.False(schema.Strict);
        var actions = schema.Schema["properties"]!["steps"]!["items"]!["properties"]!["action"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("set_value", actions);
        Assert.Contains("request_vision", actions);
        Assert.DoesNotContain("ask_user", actions);
    }

    [Fact]
    public async Task Session_keeps_instruction_but_drops_outdated_ui_state()
    {
        var chat = new FakeChatModel((_, i) => i == 0
            ? """{"status":"a","steps":[{"action":"click","target":1,"target_label":"Weiter","description":"weiter"}],"after_steps":"replan"}"""
            : """{"status":"b","steps":[],"after_steps":"verify_and_finish","final_message":"fertig"}""");
        var planner = new Planner(chat, KairoLogger.Null);
        var session = planner.StartSession("anthropic/claude-sonnet-5", null);

        await session.PlanAsync(new PlanningInput
        {
            Instruction = "Klicke auf Weiter",
            UiState = "<untrusted_data boundary=\"x\" source=\"ui\">\n[1] button \"Weiter\"\n</untrusted_data boundary=\"x\">",
        }, CancellationToken.None);
        var second = await session.PlanAsync(new PlanningInput
        {
            Instruction = "Klicke auf Weiter",
            UiState = "<untrusted_data boundary=\"x\" source=\"ui\">\n[1] button \"Fertig\"\n</untrusted_data boundary=\"x\">",
            Outcomes = [new StepOutcome(new AgentAction { Kind = ActionKind.Click, TargetLabel = "Weiter" }, true, "InvokePattern", true)],
        }, CancellationToken.None);

        Assert.Equal("fertig", second.FinalMessage);
        var req = chat.Requests[1];
        Assert.Equal(ChatRole.System, req.Messages[0].Role);
        Assert.Contains("<user_instruction>", req.Messages[1].TextContent);
        Assert.DoesNotContain("[1] button \"Weiter\"", req.Messages[1].TextContent); // old UI state removed
        Assert.Contains("(outdated – omitted)", req.Messages[1].TextContent);
        Assert.Contains("[1] button \"Fertig\"", req.Messages[^1].TextContent);
        Assert.Contains("click \"Weiter\" → OK", req.Messages[^1].TextContent);
        Assert.Equal("planning", req.Purpose);
    }
}
