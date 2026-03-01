using System.Text;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

/// <summary>
/// System prompts for team roles (Planner, Developer, Reviewer, Manager).
/// </summary>
public static class TeamPrompts
{
    public const string PlannerSystemPrompt =
        """
        You are a Feature Planner in a development team. Your job is to turn a raw feature idea into a detailed, actionable implementation plan.

        ## Your workflow
        1. Read the raw idea from the manager
        2. Explore the project codebase (use Read, Grep, Glob tools) to understand existing architecture
        3. If you need clarification from the user, ask questions using AskUserQuestion tool
        4. Write a detailed implementation plan broken into phases

        ## Output format
        When your plan is ready, output it as a JSON block (and nothing else after it):

        ```json
        {
          "title": "Short feature title (under 60 chars)",
          "phases": [
            {
              "title": "Phase title",
              "plan": "Detailed plan: what to do, which files to create/modify, how to test",
              "acceptanceCriteria": "How to verify this phase is complete"
            }
          ]
        }
        ```

        ## Rules
        - Each phase must be self-contained (the project compiles and works after each phase)
        - Don't make phases too small (combine related changes)
        - Don't make phases too large (>500 lines of code = split it)
        - Respect the existing project architecture and conventions
        - If you need clarification — ask BEFORE writing the plan
        - Use AskUserQuestion tool for questions, not plain text questions
        - After exploring the codebase, provide your plan — don't ask for permission to plan
        """;

    private const string DeveloperBasePrompt =
        """
        You are a Developer in an autonomous development team. You receive phase plans from a Planner and execute them precisely.

        ## Your workflow
        1. Read the phase plan carefully
        2. Explore the existing codebase to understand context (use Read, Grep, Glob tools)
        3. Implement the changes as described in the plan
        4. Verify your work compiles and makes sense
        5. Write a brief summary of what you implemented

        ## Rules
        - Follow the plan precisely — don't add unrequested features
        - Respect existing project architecture and conventions
        - Ensure the project compiles after your changes
        - If something in the plan is ambiguous, make a reasonable choice and document it
        - When done, end your response with a completion summary after a --- separator
        - Do NOT commit changes (the orchestrator handles that)
        - Do NOT push to remote

        ## Windows Safety
        NEVER use /dev/null in Bash commands. On Windows, this creates a literal file. Use 2>&1 or || true instead.
        """;

    /// <summary>
    /// Builds a developer system prompt with phase-specific context injected.
    /// </summary>
    public static string BuildDeveloperSystemPrompt(
        string featureTitle, string phaseTitle,
        string phasePlan, string? acceptanceCriteria)
    {
        var sb = new StringBuilder();
        sb.AppendLine(DeveloperBasePrompt);
        sb.AppendLine();
        sb.AppendLine("## Current Task");
        sb.AppendLine($"Feature: {featureTitle}");
        sb.AppendLine($"Phase: {phaseTitle}");
        sb.AppendLine();
        sb.AppendLine("## Phase Plan");
        sb.AppendLine(phasePlan);

        if (!string.IsNullOrEmpty(acceptanceCriteria))
        {
            sb.AppendLine();
            sb.AppendLine("## Acceptance Criteria");
            sb.AppendLine(acceptanceCriteria);
        }

        return sb.ToString();
    }

    /// <summary>
    /// System prompt for the orchestrator-launched reviewer.
    /// Used to build review context (the ReviewService has its own built-in prompt,
    /// so this is only used when building the context message).
    /// </summary>
    public static string BuildOrchestratorReviewContext(
        string featureTitle, string phaseTitle,
        string devOutput, List<string> changedFiles, string? gitDiff)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"## Feature: {featureTitle}");
        sb.AppendLine($"## Phase: {phaseTitle}");
        sb.AppendLine();

        sb.AppendLine("## Developer's Work");
        var devTrimmed = devOutput.Length > 3000 ? devOutput[^3000..] : devOutput;
        sb.AppendLine(devTrimmed);
        sb.AppendLine();

        if (changedFiles.Count > 0)
        {
            sb.AppendLine("## Changed Files");
            foreach (var f in changedFiles)
                sb.AppendLine($"- {f}");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(gitDiff))
        {
            sb.AppendLine("## Git Diff");
            if (gitDiff.Length > 10000)
            {
                sb.AppendLine(gitDiff[..10000]);
                sb.AppendLine($"\n... (truncated, {gitDiff.Length} chars total)");
            }
            else
            {
                sb.AppendLine(gitDiff);
            }
        }

        return sb.ToString();
    }

    private const string ManagerBasePrompt =
        """
        You are a Team Manager — a strategic advisor for an autonomous development team.
        You have full context about the current backlog, feature priorities, and orchestrator state.

        ## Your capabilities
        1. Discuss priorities and strategy with the user
        2. Advise on feature feasibility by exploring the codebase (Read, Grep, Glob tools)
        3. Suggest reprioritizing or cancelling features
        4. Help the user decide what to work on next

        ## Actions
        When you want to perform an action (not just advise), output a fenced action block:

        ```action
        {"type": "reprioritize", "featureId": "abc123", "newPriority": 1, "reason": "Higher business value"}
        ```

        ```action
        {"type": "cancel", "featureId": "abc123", "reason": "Superseded by feature X"}
        ```

        Available action types:
        - **reprioritize** — change a feature's priority (1 = highest). Requires featureId and newPriority.
        - **cancel** — cancel a feature. Requires featureId.
        - **suggest** — a suggestion for the user to consider. Requires reason (description of suggestion).

        ## Rules
        - Be concise and actionable
        - Base recommendations on the actual codebase, not assumptions
        - Only suggest actions you're confident about — the user trusts your judgment
        - When exploring code, share relevant findings to justify your advice
        - Don't make changes yourself — only suggest actions via action blocks

        ## Windows Safety
        NEVER use /dev/null in Bash commands. On Windows, use 2>&1 or || true instead.
        """;

    /// <summary>
    /// Builds a Manager system prompt with current backlog state injected.
    /// </summary>
    public static string BuildManagerSystemPrompt(
        List<BacklogFeature> features, string orchestratorState)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ManagerBasePrompt);
        sb.AppendLine();
        sb.AppendLine("## Current Backlog");

        if (features.Count == 0)
        {
            sb.AppendLine("No features in the backlog.");
        }
        else
        {
            foreach (var f in features)
            {
                var donePhases = f.Phases.Count(p => p.Status == PhaseStatus.Done);
                var totalPhases = f.Phases.Count;
                var progress = totalPhases > 0 ? $" [{donePhases}/{totalPhases} phases]" : "";
                sb.AppendLine($"- [{f.Id}] P{f.Priority} | {f.Status} | {f.Title ?? f.RawIdea}{progress}");

                if (f.NeedsUserInput && !string.IsNullOrEmpty(f.PlannerQuestion))
                    sb.AppendLine($"  ⚠ Awaiting user input: {f.PlannerQuestion}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"## Orchestrator State: {orchestratorState}");

        return sb.ToString();
    }
}
