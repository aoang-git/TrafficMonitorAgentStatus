using System.Text.Json;

namespace AgentStatus;

public static class Adapters
{
    public static List<StatusEvent> Convert(string source, JsonElement raw, string? eventOverride = null)
    {
        if (source == "generic") return [raw.Deserialize<StatusEvent>(JsonWire.Options) ?? throw new InvalidDataException("Missing event")];
        if (source is not ("claude-code" or "cursor" or "gemini-cli")) throw new InvalidDataException("Unsupported source");
        string S(string k) => raw.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
        string name = eventOverride ?? S("hook_event_name");
        string session = S("session_id");
        if (session.Length == 0) session = S("conversation_id");
        if (session.Length == 0) throw new InvalidDataException("Hook has no session identifier");
        string tool = S("tool_name");
        string call = S("tool_use_id");
        if (call.Length == 0) call = S("tool_call_id");
        string agent = S("agent_id");
        string project = S("cwd");
        project = project.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? "";
        string notification = S("notification_type");
        string kind = name switch
        {
            "SessionStart" or "sessionStart" => "session.started",
            "SessionEnd" or "sessionEnd" => "session.ended",
            "UserPromptSubmit" or "beforeSubmitPrompt" or "BeforeAgent" => "turn.started",
            "PreToolUse" or "preToolUse" or "BeforeTool" => "tool.started",
            "PostToolUse" or "postToolUse" or "AfterTool" => "tool.finished",
            "PostToolUseFailure" or "postToolUseFailure" => "tool.failed",
            "PermissionRequest" => "permission.requested",
            "Stop" or "stop" or "AfterAgent" => "turn.finished",
            "StopFailure" => "turn.failed",
            "TaskCompleted" => "task.completed",
            "Elicitation" => "input.requested",
            "ElicitationResult" => "input.resolved",
            "Notification" when notification == "permission_prompt" => "permission.requested",
            "Notification" when notification is "elicitation_dialog" or "agent_needs_input" => "input.requested",
            _ => ""
        };
        if (kind.Length == 0) return [];
        var e = new StatusEvent { Source = source, SessionId = session, InstanceId = "local", EventType = kind, RawEventName = name, ProjectLabel = project, ToolName = tool, ToolCallId = call.Length == 0 ? null : call, AgentId = agent.Length == 0 ? null : agent, RequestId = S("elicitation_id") is { Length: > 0 } id ? id : null, TaskId = S("task_id") };
        // No completion inference from arbitrary response text, and no permission decision output.
        bool asks = tool is "AskUserQuestion" or "ask_user" or "ask_question";
        if (asks && kind == "tool.started") return [e, e with { EventId = Guid.NewGuid().ToString("N"), EventType = "input.requested" }];
        if (asks && kind is "tool.finished" or "tool.failed") return [e, e with { EventId = Guid.NewGuid().ToString("N"), EventType = "input.resolved" }];
        return [e];
    }
}
