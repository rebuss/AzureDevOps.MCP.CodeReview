namespace REBUSS.Pure.Core.Services.AgentInvocation;

/// <summary>
/// Names the AI agent the MCP server is currently wired to invoke. Used by
/// tool handlers to label review responses ("claude-assisted" / "copilot-assisted")
/// so the agent reading the response knows which backend produced it — and so
/// the user is never told they are looking at a Copilot review when Claude did
/// the work.
/// <para>
/// Resolved once at DI composition from <c>--agent</c>, registered as a singleton.
/// Values match the CLI flag spelling: <c>"copilot"</c> or <c>"claude"</c>.
/// </para>
/// </summary>
public sealed record AgentIdentity(string Name);
