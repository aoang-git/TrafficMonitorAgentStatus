using System.Text.Json;

namespace AgentStatus;

public static class Adapters
{
    static readonly Dictionary<string, string> CodexSurfaces = new(StringComparer.OrdinalIgnoreCase);
    static readonly object CodexSurfaceGate = new();

    public static List<StatusEvent> Convert(string source, JsonElement raw, string? eventOverride = null)
    {
        if (source == "generic") return [raw.Deserialize<StatusEvent>(JsonWire.Options) ?? throw new InvalidDataException("Missing event")];
        if (source is not ("claude-code" or "codex-cli" or "cursor" or "gemini-cli")) throw new InvalidDataException("Unsupported source");
        static string StringValue(JsonElement value, params string[] names)
        {
            foreach (var name in names)
                if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                    return property.GetString() ?? "";
            return "";
        }
        string S(params string[] keys) => StringValue(raw, keys);
        string Detail(params string[] keys) => raw.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object
            ? StringValue(details, keys)
            : "";
        string name = eventOverride ?? S("hook_event_name");
        string session = S("session_id");
        if (session.Length == 0) session = S("conversation_id");
        // Some hook events carry no session identifier at all (for example a
        // freshly installed tool that has not opened a conversation yet). That
        // is not an error: drop the event instead of failing the delivery.
        if (session.Length == 0) return [];
        // Cursor imports compatible hooks from ~/.claude. Those hooks still pass
        // --source claude-code, but Cursor identifies its payload explicitly.
        // Normalize both deliveries to the same source/session so the taskbar
        // cannot show a Cursor run as Claude Code.
        if (source == "claude-code" && S("cursor_version").Length > 0) source = "cursor";
        if (source == "codex-cli") source = ResolveCodexSurface(S("transcript_path"));
        string tool = S("tool_name");
        if (tool.Length == 0) tool = Detail("tool_name", "toolName", "name");
        string call = S("tool_use_id");
        if (call.Length == 0) call = S("tool_call_id");
        if (call.Length == 0) call = S("call_id");
        if (call.Length == 0) call = Detail("tool_use_id", "toolUseId", "tool_call_id", "toolCallId", "call_id", "callId");
        string agent = S("agent_id");
        string project = S("cwd");
        if (project.Length == 0 && raw.TryGetProperty("workspace_roots", out var roots) && roots.ValueKind == JsonValueKind.Array)
        {
            foreach (var root in roots.EnumerateArray())
                if (root.ValueKind == JsonValueKind.String)
                {
                    project = root.GetString() ?? "";
                    break;
                }
        }
        project = project.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? "";
        string notification = S("notification_type");
        string status = S("status");
        bool failedTool = name == "AfterTool"
            && raw.TryGetProperty("tool_response", out var response)
            && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("error", out var error)
            && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        string kind = name switch
        {
            "SessionStart" or "sessionStart" => "session.started",
            "SessionEnd" or "sessionEnd" => "session.ended",
            "UserPromptSubmit" or "beforeSubmitPrompt" or "BeforeAgent" => "turn.started",
            "PreToolUse" or "preToolUse" or "BeforeTool" => "tool.started",
            "AfterTool" when failedTool => "tool.failed",
            "PostToolUse" or "postToolUse" or "AfterTool" => "tool.finished",
            "PostToolUseFailure" or "postToolUseFailure" => "tool.failed",
            "PermissionRequest" => "permission.requested",
            "PermissionDenied" => "permission.resolved",
            // A cancel is a normal end of turn, not a failure. Only an explicit
            // error status, or StopFailure, means the turn actually failed.
            "stop" when status is "error" => "turn.failed",
            "Stop" or "stop" or "AfterAgent" => "turn.finished",
            "Interrupt" => "turn.finished",
            "StopFailure" => "turn.failed",
            "TaskCompleted" => "task.completed",
            "Elicitation" => "input.requested",
            "ElicitationResult" => "input.resolved",
            "Notification" when notification is "permission_prompt" or "ToolPermission" => "permission.requested",
            "Notification" when notification is "elicitation_dialog" or "agent_needs_input" => "input.requested",
            _ => ""
        };
        if (kind.Length == 0) return [];
        string request = S("request_id", "elicitation_id");
        if (request.Length == 0) request = Detail("request_id", "requestId", "elicitation_id", "elicitationId");
        string turn = S("turn_id", "generation_id");
        string version = S("source_version", "cursor_version", "version");
        var occurredAt = DateTimeOffset.TryParse(S("timestamp"), out var parsed) ? parsed : DateTimeOffset.UtcNow;
        var e = new StatusEvent
        {
            Source = source,
            SourceVersion = version,
            SessionId = session,
            InstanceId = source switch { "codex-desktop" => "desktop", "codex-cli" => "cli", _ => "local" },
            TurnId = turn.Length == 0 ? null : turn,
            EventType = kind,
            RawEventName = name,
            ProjectLabel = project,
            ToolName = tool,
            ToolCallId = call.Length == 0 ? null : call,
            AgentId = agent.Length == 0 ? null : agent,
            RequestId = request.Length > 0 ? request : call.Length > 0 && (kind is "permission.requested" or "input.requested") ? call : null,
            TaskId = S("task_id"),
            OccurredAt = occurredAt
        };
        // No completion inference from arbitrary response text, and no permission decision output.
        bool asks = tool is "AskUserQuestion" or "ask_user" or "ask_question" or "request_user_input";
        if (asks && kind == "tool.started") return [e, e with { EventId = Guid.NewGuid().ToString("N"), EventType = "input.requested" }];
        if (asks && kind is "tool.finished" or "tool.failed") return [e, e with { EventId = Guid.NewGuid().ToString("N"), EventType = "input.resolved" }];
        return [e];
    }

    static string ResolveCodexSurface(string transcriptPath)
    {
        if (transcriptPath.Length == 0) return "codex-cli";
        lock (CodexSurfaceGate)
            if (CodexSurfaces.TryGetValue(transcriptPath, out var cached)) return cached;
        try
        {
            var parser = new CodexDesktopParser(transcriptPath);
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            for (var i = 0; i < 16 && !reader.EndOfStream; i++)
            {
                var line = reader.ReadLine();
                if (line != null) parser.ReadMetadata(line);
            }
            var result = parser.IsDesktop ? "codex-desktop" : "codex-cli";
            lock (CodexSurfaceGate)
            {
                if (CodexSurfaces.Count >= 256) CodexSurfaces.Remove(CodexSurfaces.Keys.First());
                CodexSurfaces[transcriptPath] = result;
            }
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "codex-cli";
        }
    }
}
