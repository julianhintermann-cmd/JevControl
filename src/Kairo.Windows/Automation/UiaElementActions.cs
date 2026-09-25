using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using Kairo.Core.Abstractions;
using Kairo.Core.Models;
using Kairo.Core.Perception;
using Kairo.Core.Telemetry;
using Kairo.Windows.Input;
using Kairo.Windows.Windows;
using P = Interop.UIAutomationClient.UIA_PropertyIds;
using PT = Interop.UIAutomationClient.UIA_PatternIds;
using CT = Interop.UIAutomationClient.UIA_ControlTypeIds;

namespace Kairo.Windows.Automation;

/// <summary>
/// Executes element actions through UI Automation patterns (Value, Invoke, Toggle, SelectionItem,
/// ExpandCollapse, Scroll). Simulated keyboard/mouse input is only the fallback – or used on purpose
/// when a verification showed that the structured call had no effect.
/// </summary>
public sealed class UiaElementActions
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(4);

    private readonly UiaCore _core;
    private readonly WindowService _windows;
    private readonly InputSimulator _input;
    private readonly KairoLogger _log;

    public UiaElementActions(UiaCore core, WindowService windows, InputSimulator input, KairoLogger log)
    {
        _core = core;
        _windows = windows;
        _input = input;
        _log = log;
    }

    public async Task<ActionResult> ExecuteAsync(AgentAction action, UiElement element, ActionContext context, CancellationToken cancellationToken)
    {
        context.Gate.ThrowIfClosed();
        var live = await UiaCore.RunAsync(() => _core.Resolve(element.Locator, context.TargetWindow.Handle), cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return ActionResult.Fail(ActionErrorKind.ElementNotFound, $"Element „{element.DisplayLabel}“ ist nicht mehr vorhanden.");
        }

        try
        {
            return action.Kind switch
            {
                ActionKind.SetValue => await SetValueAsync(live, element, action.Value ?? "", context, cancellationToken).ConfigureAwait(false),
                ActionKind.Click => await ClickAsync(live, element, context, cancellationToken).ConfigureAwait(false),
                ActionKind.SetChecked => await SetCheckedAsync(live, element, action.Checked ?? true, context, cancellationToken).ConfigureAwait(false),
                ActionKind.SelectOption => await SelectOptionAsync(live, element, action.Option ?? action.Value ?? "", context, cancellationToken).ConfigureAwait(false),
                ActionKind.Focus => await UiaCore.RunAsync(() => { live.SetFocus(); return ActionResult.Ok("", "SetFocus"); }, cancellationToken).ConfigureAwait(false),
                _ => ActionResult.Fail(ActionErrorKind.NotSupported, $"{action.Kind} ist keine Elementaktion."),
            };
        }
        catch (COMException ex)
        {
            _core.Forget(element.Locator);
            _log.Warn("uia", $"{action.Kind} COM error 0x{ex.HResult:X8}");
            return ActionResult.Fail(ActionErrorKind.Failed, $"UI Automation meldet einen Fehler (0x{ex.HResult:X8}).");
        }
    }

    // ---------------------------------------------------------------- set value
    private async Task<ActionResult> SetValueAsync(IUIAutomationElement live, UiElement element, string value, ActionContext context, CancellationToken cancellationToken)
    {
        if (!context.PreferInputSimulation)
        {
            var structured = await UiaCore.RunAsync(() =>
            {
                if (live.GetCurrentPropertyValue(P.UIA_IsValuePatternAvailablePropertyId) is true &&
                    live.GetCurrentPropertyValue(P.UIA_ValueIsReadOnlyPropertyId) is not true &&
                    live.GetCurrentPattern(PT.UIA_ValuePatternId) is IUIAutomationValuePattern vp)
                {
                    vp.SetValue(value);
                    return live.GetCurrentPropertyValue(P.UIA_ValueValuePropertyId) is string current && Core.Agent.Verifier.ValuesMatch(value, current);
                }
                return false;
            }, cancellationToken).ConfigureAwait(false);
            if (structured) { return ActionResult.Ok("", "ValuePattern"); }
        }

        // Keyboard: focus, select all, type (also used for rich text editors without ValuePattern).
        context.Gate.ThrowIfClosed();
        if (await RequireForegroundAsync(context, cancellationToken).ConfigureAwait(false) is { } notForeground) { return notForeground; }
        await UiaCore.RunAsync(() => { live.SetFocus(); return true; }, cancellationToken).ConfigureAwait(false);
        await Task.Delay(30, cancellationToken).ConfigureAwait(false);
        if (!await FocusIsOnAsync(live, cancellationToken).ConfigureAwait(false))
        {
            var click = await ClickCenterAsync(live, element, context, cancellationToken).ConfigureAwait(false);
            if (!click.Success) { return click; }
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            if (!await FocusIsOnAsync(live, cancellationToken).ConfigureAwait(false) &&
                _windows.GetForegroundWindow()?.Handle != context.TargetWindow.Handle)
            {
                // Typically a modal dialog in front of the target: typing would land there.
                return ActionResult.Fail(ActionErrorKind.Failed, "Das Feld lässt sich nicht fokussieren – vermutlich ist ein Dialog geöffnet. Es wurde nichts eingegeben.");
            }
        }
        context.Gate.ThrowIfClosed();
        if (await RequireForegroundAsync(context, cancellationToken).ConfigureAwait(false) is { } lostForeground) { return lostForeground; }
        _input.Press("ctrl+a");
        _input.Press("delete");
        await _input.TypeTextAsync(value, element.IsMultiline || element.Role == ElementRole.Document, context.TypingDelayMs, () => context.Gate.IsOpen, cancellationToken).ConfigureAwait(false);
        await WaitForTypedValueAsync(live, value, cancellationToken).ConfigureAwait(false);
        return ActionResult.Ok("", "SendInput");
    }

    /// <summary>
    /// Simulated input is only sent when the target window really is in the foreground –
    /// otherwise keystrokes would land in whatever window the user is looking at.
    /// </summary>
    private async Task<ActionResult?> RequireForegroundAsync(ActionContext context, CancellationToken cancellationToken)
    {
        if (context.TargetWindow.Handle == 0 || await _windows.ActivateAsync(context.TargetWindow.Handle, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        _log.Warn("uia", "target window could not be activated, simulated input suppressed");
        return ActionResult.Fail(ActionErrorKind.Failed, "Das Zielfenster konnte nicht in den Vordergrund geholt werden – Kairo hat deshalb keine Tastatur- oder Mauseingabe gesendet.");
    }

    /// <summary>
    /// SendInput only queues the keystrokes; the target processes them asynchronously. Wait until the
    /// field reports the typed value (or a short timeout) so that verification does not read a stale value.
    /// </summary>
    private static async Task WaitForTypedValueAsync(IUIAutomationElement live, string expected, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
        while (true)
        {
            var state = await UiaCore.RunAsync<bool?>(() =>
            {
                try
                {
                    if (live.GetCurrentPropertyValue(P.UIA_IsValuePatternAvailablePropertyId) is not true) { return null; }
                    return live.GetCurrentPropertyValue(P.UIA_ValueValuePropertyId) is string current && Core.Agent.Verifier.ValuesMatch(expected, current);
                }
                catch (COMException)
                {
                    return null;
                }
            }, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (state == true || DateTime.UtcNow >= deadline) { return; }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<bool> FocusIsOnAsync(IUIAutomationElement live, CancellationToken cancellationToken) =>
        UiaCore.RunAsync(() =>
        {
            try { return live.GetCurrentPropertyValue(P.UIA_HasKeyboardFocusPropertyId) is true; }
            catch (COMException) { return false; }
        }, cancellationToken);

    // ---------------------------------------------------------------- click
    private async Task<ActionResult> ClickAsync(IUIAutomationElement live, UiElement element, ActionContext context, CancellationToken cancellationToken)
    {
        if (!context.PreferInputSimulation)
        {
            var strategy = await RunPatternWithTimeoutAsync(() =>
            {
                if (live.GetCurrentPropertyValue(P.UIA_IsInvokePatternAvailablePropertyId) is true && live.GetCurrentPattern(PT.UIA_InvokePatternId) is IUIAutomationInvokePattern invoke)
                {
                    invoke.Invoke();
                    return "InvokePattern";
                }
                if (live.GetCurrentPropertyValue(P.UIA_IsTogglePatternAvailablePropertyId) is true && live.GetCurrentPattern(PT.UIA_TogglePatternId) is IUIAutomationTogglePattern toggle)
                {
                    toggle.Toggle();
                    return "TogglePattern";
                }
                if (live.GetCurrentPropertyValue(P.UIA_IsSelectionItemPatternAvailablePropertyId) is true && live.GetCurrentPattern(PT.UIA_SelectionItemPatternId) is IUIAutomationSelectionItemPattern select)
                {
                    select.Select();
                    return "SelectionItemPattern";
                }
                if (live.GetCurrentPropertyValue(P.UIA_IsExpandCollapsePatternAvailablePropertyId) is true && live.GetCurrentPattern(PT.UIA_ExpandCollapsePatternId) is IUIAutomationExpandCollapsePattern expand)
                {
                    if (expand.CurrentExpandCollapseState == ExpandCollapseState.ExpandCollapseState_Expanded) { expand.Collapse(); }
                    else { expand.Expand(); }
                    return "ExpandCollapsePattern";
                }
                if (live.GetCurrentPropertyValue(P.UIA_IsLegacyIAccessiblePatternAvailablePropertyId) is true && live.GetCurrentPattern(PT.UIA_LegacyIAccessiblePatternId) is IUIAutomationLegacyIAccessiblePattern legacy &&
                    !string.IsNullOrEmpty(legacy.CurrentDefaultAction))
                {
                    legacy.DoDefaultAction();
                    return "LegacyIAccessible";
                }
                return null;
            }, cancellationToken).ConfigureAwait(false);

            if (strategy is not null) { return ActionResult.Ok("", strategy, structureChanged: true); }
        }

        return await ClickCenterAsync(live, element, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pattern calls such as Invoke can block until a modal dialog opened by the click is closed (known UIA behavior).
    /// They therefore run with a timeout; a timeout counts as success because the action was delivered.
    /// </summary>
    private static async Task<string?> RunPatternWithTimeoutAsync(Func<string?> call, CancellationToken cancellationToken)
    {
        var task = Task.Run(call, CancellationToken.None);
        try
        {
            return await task.WaitAsync(PatternTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return "Pattern (blockiert – vermutlich modaler Dialog)";
        }
    }

    private async Task<ActionResult> ClickCenterAsync(IUIAutomationElement live, UiElement element, ActionContext context, CancellationToken cancellationToken)
    {
        var point = await UiaCore.RunAsync<(int X, int Y)?>(() =>
        {
            try
            {
                if (live.GetCurrentPropertyValue(P.UIA_IsScrollItemPatternAvailablePropertyId) is true && live.GetCurrentPattern(PT.UIA_ScrollItemPatternId) is IUIAutomationScrollItemPattern scroll)
                {
                    scroll.ScrollIntoView();
                }
                if (live.GetClickablePoint(out var p) != 0) { return (p.x, p.y); }
                var r = live.CurrentBoundingRectangle;
                if (r.right > r.left && r.bottom > r.top) { return ((r.left + r.right) / 2, (r.top + r.bottom) / 2); }
            }
            catch (COMException)
            {
            }
            return element.Bounds.IsEmpty ? null : element.Bounds.Center;
        }, cancellationToken).ConfigureAwait(false);

        if (point is null) { return ActionResult.Fail(ActionErrorKind.Failed, "Keine klickbare Position gefunden."); }
        context.Gate.ThrowIfClosed();
        if (await RequireForegroundAsync(context, cancellationToken).ConfigureAwait(false) is { } notForeground) { return notForeground; }
        context.Gate.ThrowIfClosed();
        _input.Click(point.Value.X, point.Value.Y);
        return ActionResult.Ok("", "Mausklick", structureChanged: true);
    }

    // ---------------------------------------------------------------- checkbox / radio
    private async Task<ActionResult> SetCheckedAsync(IUIAutomationElement live, UiElement element, bool desired, ActionContext context, CancellationToken cancellationToken)
    {
        var done = await UiaCore.RunAsync(() =>
        {
            if (live.GetCurrentPropertyValue(P.UIA_IsTogglePatternAvailablePropertyId) is true && live.GetCurrentPattern(PT.UIA_TogglePatternId) is IUIAutomationTogglePattern toggle)
            {
                for (var i = 0; i < 3 && (toggle.CurrentToggleState == ToggleState.ToggleState_On) != desired; i++) { toggle.Toggle(); }
                return (toggle.CurrentToggleState == ToggleState.ToggleState_On) == desired;
            }
            if (desired && live.GetCurrentPropertyValue(P.UIA_IsSelectionItemPatternAvailablePropertyId) is true && live.GetCurrentPattern(PT.UIA_SelectionItemPatternId) is IUIAutomationSelectionItemPattern select)
            {
                select.Select();
                return select.CurrentIsSelected != 0;
            }
            return false;
        }, cancellationToken).ConfigureAwait(false);

        if (done && !context.PreferInputSimulation) { return ActionResult.Ok("", "TogglePattern"); }
        if (element.IsChecked == desired && context.PreferInputSimulation is false) { return ActionResult.Ok("", "unverändert"); }
        return await ClickCenterAsync(live, element, context, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- dropdowns / lists / radio groups
    private async Task<ActionResult> SelectOptionAsync(IUIAutomationElement live, UiElement element, string option, ActionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(option)) { return ActionResult.Fail(ActionErrorKind.InvalidArguments, "Keine Option angegeben."); }

        var result = await UiaCore.RunAsync(() => SelectStructured(live, element, option), cancellationToken).ConfigureAwait(false);
        if (result is not null) { return result; }

        // Keyboard fallback: open the dropdown, type the option text, confirm.
        context.Gate.ThrowIfClosed();
        if (await RequireForegroundAsync(context, cancellationToken).ConfigureAwait(false) is { } notForeground) { return notForeground; }
        await UiaCore.RunAsync(() => { live.SetFocus(); return true; }, cancellationToken).ConfigureAwait(false);
        _input.Press("alt+down");
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        await _input.TypeTextAsync(option, false, context.TypingDelayMs, () => context.Gate.IsOpen, cancellationToken).ConfigureAwait(false);
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        context.Gate.ThrowIfClosed();
        _input.Press("enter");
        return ActionResult.Ok("", "Tastatur");
    }

    private ActionResult? SelectStructured(IUIAutomationElement live, UiElement element, string option)
    {
        var automation = _core.Automation;

        // Radio group: the planner may target one radio button and name the wanted option.
        if (element.Role == ElementRole.RadioButton)
        {
            var parent = automation.ControlViewWalker.GetParentElement(live);
            var radios = parent?.FindAll(TreeScope.TreeScope_Children, automation.CreatePropertyCondition(P.UIA_ControlTypePropertyId, CT.UIA_RadioButtonControlTypeId));
            var match = BestMatch(radios, option);
            if (match is not null && match.GetCurrentPattern(PT.UIA_SelectionItemPatternId) is IUIAutomationSelectionItemPattern sel)
            {
                sel.Select();
                return ActionResult.Ok("", "SelectionItemPattern");
            }
        }

        // Editable combo boxes accept the text directly.
        if (element.Role == ElementRole.ComboBox &&
            live.GetCurrentPropertyValue(P.UIA_IsValuePatternAvailablePropertyId) is true &&
            live.GetCurrentPropertyValue(P.UIA_ValueIsReadOnlyPropertyId) is not true &&
            live.GetCurrentPattern(PT.UIA_ValuePatternId) is IUIAutomationValuePattern vp &&
            (element.Options is null || element.Options.Count == 0))
        {
            vp.SetValue(option);
            return ActionResult.Ok("", "ValuePattern");
        }

        IUIAutomationExpandCollapsePattern? expand = null;
        if (live.GetCurrentPropertyValue(P.UIA_IsExpandCollapsePatternAvailablePropertyId) is true)
        {
            expand = live.GetCurrentPattern(PT.UIA_ExpandCollapsePatternId) as IUIAutomationExpandCollapsePattern;
            try { expand?.Expand(); }
            catch (COMException) { expand = null; }
            Thread.Sleep(150);
        }

        var itemCondition = automation.CreateOrCondition(
            automation.CreatePropertyCondition(P.UIA_ControlTypePropertyId, CT.UIA_ListItemControlTypeId),
            automation.CreatePropertyCondition(P.UIA_ControlTypePropertyId, CT.UIA_MenuItemControlTypeId));
        var items = live.FindAll(TreeScope.TreeScope_Descendants, itemCondition);
        var item = BestMatch(items, option);
        if (item is null && expand is not null)
        {
            // Some combo boxes render their list as a separate popup window: search from the root.
            var root = automation.GetRootElement();
            var popups = root.FindAll(TreeScope.TreeScope_Children, automation.CreatePropertyCondition(P.UIA_ControlTypePropertyId, CT.UIA_ListControlTypeId));
            for (var i = 0; popups is not null && i < popups.Length && item is null; i++)
            {
                item = BestMatch(popups.GetElement(i).FindAll(TreeScope.TreeScope_Descendants, itemCondition), option);
            }
        }

        if (item is null)
        {
            try { expand?.Collapse(); }
            catch (COMException) { }
            return null;
        }

        if (item.GetCurrentPropertyValue(P.UIA_IsScrollItemPatternAvailablePropertyId) is true && item.GetCurrentPattern(PT.UIA_ScrollItemPatternId) is IUIAutomationScrollItemPattern scroll)
        {
            try { scroll.ScrollIntoView(); } catch (COMException) { }
        }
        if (item.GetCurrentPropertyValue(P.UIA_IsSelectionItemPatternAvailablePropertyId) is true && item.GetCurrentPattern(PT.UIA_SelectionItemPatternId) is IUIAutomationSelectionItemPattern selectItem)
        {
            selectItem.Select();
        }
        else if (item.GetCurrentPattern(PT.UIA_InvokePatternId) is IUIAutomationInvokePattern invoke)
        {
            invoke.Invoke();
        }
        else
        {
            return null;
        }

        try
        {
            if (expand is not null && expand.CurrentExpandCollapseState == ExpandCollapseState.ExpandCollapseState_Expanded) { expand.Collapse(); }
        }
        catch (COMException) { }
        return ActionResult.Ok("", "SelectionItemPattern");
    }

    private static IUIAutomationElement? BestMatch(IUIAutomationElementArray? items, string option)
    {
        if (items is null || items.Length == 0) { return null; }
        var wanted = ElementMatcher.Normalize(option);
        IUIAutomationElement? best = null;
        var bestScore = 0.0;
        for (var i = 0; i < items.Length; i++)
        {
            var item = items.GetElement(i);
            string name;
            try { name = item.CurrentName ?? ""; }
            catch (COMException) { continue; }
            var normalized = ElementMatcher.Normalize(name);
            var score = normalized == wanted ? 1.0 : ElementMatcher.TextSimilarity(wanted, normalized);
            if (score > bestScore) { best = item; bestScore = score; }
        }
        return bestScore >= 0.6 ? best : null;
    }

    // ---------------------------------------------------------------- scroll
    public async Task<ActionResult> ScrollAsync(UiElement? element, string direction, ActionContext context, CancellationToken cancellationToken)
    {
        var down = !direction.StartsWith("up", StringComparison.OrdinalIgnoreCase) && !direction.StartsWith("hoch", StringComparison.OrdinalIgnoreCase) &&
                   !direction.StartsWith("oben", StringComparison.OrdinalIgnoreCase);
        var done = await UiaCore.RunAsync(() =>
        {
            var automation = _core.Automation;
            IUIAutomationElement? target = element is null ? null : _core.Resolve(element.Locator, context.TargetWindow.Handle);
            target ??= automation.ElementFromHandle(context.TargetWindow.Handle);
            // Walk up (element) or down (window) to find something scrollable.
            var scrollable = target;
            for (var i = 0; scrollable is not null && i < 12; i++)
            {
                if (scrollable.GetCurrentPropertyValue(P.UIA_IsScrollPatternAvailablePropertyId) is true &&
                    scrollable.GetCurrentPattern(PT.UIA_ScrollPatternId) is IUIAutomationScrollPattern sp && sp.CurrentVerticallyScrollable != 0)
                {
                    sp.Scroll(ScrollAmount.ScrollAmount_NoAmount, down ? ScrollAmount.ScrollAmount_LargeIncrement : ScrollAmount.ScrollAmount_LargeDecrement);
                    return true;
                }
                scrollable = automation.ControlViewWalker.GetParentElement(scrollable);
            }
            var condition = automation.CreatePropertyCondition(P.UIA_IsScrollPatternAvailablePropertyId, true);
            var inner = target.FindFirst(TreeScope.TreeScope_Descendants, condition);
            if (inner?.GetCurrentPattern(PT.UIA_ScrollPatternId) is IUIAutomationScrollPattern innerScroll && innerScroll.CurrentVerticallyScrollable != 0)
            {
                innerScroll.Scroll(ScrollAmount.ScrollAmount_NoAmount, down ? ScrollAmount.ScrollAmount_LargeIncrement : ScrollAmount.ScrollAmount_LargeDecrement);
                return true;
            }
            return false;
        }, cancellationToken).ConfigureAwait(false);

        if (done) { return ActionResult.Ok("", "ScrollPattern", structureChanged: true); }

        context.Gate.ThrowIfClosed();
        if (await RequireForegroundAsync(context, cancellationToken).ConfigureAwait(false) is { } notForeground) { return notForeground; }
        var center = (element?.Bounds is { IsEmpty: false } b ? b : context.TargetWindow.Bounds).Center;
        _input.Wheel(center.X, center.Y, down ? -5 : 5);
        return ActionResult.Ok("", "Mausrad", structureChanged: true);
    }
}
