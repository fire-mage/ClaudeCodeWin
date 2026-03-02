using System.Text;
using ClaudeCodeWin.Models;
using ClaudeCodeWin.ViewModels;

namespace ClaudeCodeWin.Services;

/// <summary>
/// System prompts for team roles (Planner, Developer, Reviewer).
/// </summary>
public static class TeamPrompts
{
    public const string TeamModelId = "claude-opus-4-6";

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

        ## Notes for User
        If you have non-blocking observations about trade-offs, alternative approaches considered, or dependencies, output them as `USER_NOTE: your message here` (one per line). These notes will be delivered asynchronously — do NOT wait for a response.
        """;

    /// <summary>
    /// Builds a system prompt for the Plan Reviewer role.
    /// The reviewer checks implementation plans for completeness, feasibility, and missed requirements.
    /// </summary>
    public static string BuildPlanReviewerSystemPrompt()
    {
        return """
            You are a Plan Reviewer in a development team. Your job is to critically review implementation plans before they go into development.

            ## Your workflow
            1. Read the original idea and any analysis summary carefully
            2. Review each phase of the implementation plan
            3. Explore the project codebase (use Read, Grep, Glob tools) to verify the plan's assumptions
            4. Check for completeness, feasibility, and missed requirements
            5. Output your verdict as JSON

            ## What to check
            - Does the plan cover all aspects of the original idea?
            - Are the phases properly ordered and self-contained?
            - Are there missing edge cases or error handling considerations?
            - Does the plan respect existing architecture and conventions?
            - Are acceptance criteria clear and testable?
            - Is the scope appropriate (not too large, not too small per phase)?
            - Are there any security, performance, or compatibility concerns?

            ## Output format
            When your review is ready, output it as a JSON block (and nothing else after it):

            ```json
            {
              "verdict": "approve|reject",
              "comments": "Overall assessment of the plan (2-4 sentences)",
              "suggestions": [
                "Specific improvement suggestion 1",
                "Specific improvement suggestion 2"
              ]
            }
            ```

            ## Verdict meanings
            - **approve** — plan is solid, well-structured, and ready for implementation (may still have minor suggestions)
            - **reject** — plan has significant gaps, incorrect assumptions, or missing requirements that need to be addressed

            ## Rules
            - Be constructive — if rejecting, explain what needs to change
            - Base your review on actual code exploration, not assumptions
            - Keep suggestions actionable and specific
            - An empty suggestions array is fine if the plan is solid
            - Don't reject for minor style preferences — only for substantive issues

            ## Windows Safety
            NEVER use /dev/null in Bash commands. On Windows, use 2>&1 or || true instead.

            ## Notes for User
            If you have non-blocking concerns that aren't blockers, output them as `USER_NOTE: your message here` (one per line). These notes will be delivered asynchronously — do NOT wait for a response.
            """;
    }

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

        ## User Actions
        If the implementation requires manual steps that code cannot automate (DNS records, API key registration,
        server provisioning, environment variables, third-party service configuration, database migrations on production,
        certificate setup, etc.), output them as a JSON block in your completion summary:

        ```json
        {"userActions": ["Configure DNS A record for example.com → 1.2.3.4", "Add API_KEY=xxx to .env"]}
        ```

        Only include this block when there are actual manual steps required. Do not include it for routine operations.

        ## Windows Safety
        NEVER use /dev/null in Bash commands. On Windows, this creates a literal file. Use 2>&1 or || true instead.

        ## Notes for User
        If you have non-blocking observations for the user (manual steps discovered during implementation, configuration changes needed, or things to watch out for), output them as `USER_NOTE: your message here` (one per line). These notes will be delivered asynchronously — do NOT wait for a response.
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

    /// <summary>
    /// Builds a human-readable snapshot of the current Team tab state for Claude to analyze.
    /// </summary>
    public static string BuildTeamStateSnapshot(TeamViewModel team, List<BacklogFeature> features)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Team State ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine();
        sb.AppendLine($"### Orchestrator: {team.OrchestratorStatusText}");

        // Health
        if (team.SessionHealthItems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Session Health");
            foreach (var h in team.SessionHealthItems)
            {
                var detail = string.IsNullOrEmpty(h.Detail) ? "" : $" — {h.Detail}";
                sb.AppendLine($"{h.HealthIcon} {h.Role}{detail} ({h.Elapsed})");
            }
        }

        // Group features by status
        var inProgress = features.Where(f => f.Status == FeatureStatus.InProgress).ToList();
        var awaiting = features.Where(f => f.Status == FeatureStatus.AwaitingUser).ToList();
        var queued = features.Where(f => f.Status == FeatureStatus.Queued).ToList();
        var planned = features.Where(f => f.Status is FeatureStatus.PlanReady or FeatureStatus.PlanApproved).ToList();
        var planning = features.Where(f => f.Status is FeatureStatus.Planning or FeatureStatus.PlanningFailed).ToList();
        var completed = features.Where(f => f.Status is FeatureStatus.Done or FeatureStatus.Cancelled).ToList();

        AppendFeatureGroup(sb, "In Progress", inProgress, showActivePhase: true);
        AppendFeatureGroup(sb, "Awaiting Your Input", awaiting, showQuestion: true);
        AppendFeatureGroup(sb, "Queue", queued);
        AppendFeatureGroup(sb, "Planned", planned);
        AppendFeatureGroup(sb, "Planning", planning);
        AppendFeatureGroup(sb, "Completed", completed, showStatus: true);

        // Recent orchestrator log
        var log = team.OrchestratorLog;
        if (!string.IsNullOrEmpty(log))
        {
            var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var recent = lines.Length > 15 ? lines[^15..] : lines;
            sb.AppendLine();
            sb.AppendLine("### Recent Log (last 15 lines)");
            foreach (var line in recent)
                sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static void AppendFeatureGroup(StringBuilder sb, string header, List<BacklogFeature> features,
        bool showActivePhase = false, bool showQuestion = false, bool showStatus = false)
    {
        if (features.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine($"### {header} ({features.Count})");

        foreach (var f in features.OrderBy(f => f.Priority))
        {
            var title = f.Title ?? f.RawIdea;
            if (title.Length > 80) title = title[..77] + "...";

            var donePhases = f.Phases.Count(p => p.Status == PhaseStatus.Done);
            var totalPhases = f.Phases.Count;
            var progress = totalPhases > 0 ? $" [{donePhases}/{totalPhases} phases]" : "";
            var status = showStatus ? $" — {f.Status}" : "";

            sb.AppendLine($"[{f.Id}] P{f.Priority} \"{title}\"{progress}{status}");

            if (showActivePhase)
            {
                var activePhase = f.Phases
                    .FirstOrDefault(p => p.Status is PhaseStatus.InProgress or PhaseStatus.InReview);
                if (activePhase != null)
                {
                    var elapsed = activePhase.StartedAt.HasValue
                        ? $" ({(DateTime.Now - activePhase.StartedAt.Value).TotalMinutes:F0}m)"
                        : "";
                    sb.AppendLine($"  -> Phase {activePhase.Order}: \"{activePhase.Title}\" — {activePhase.Status}{elapsed}");

                    if (activePhase.Status == PhaseStatus.Failed && !string.IsNullOrEmpty(activePhase.ErrorMessage))
                        sb.AppendLine($"  Error: {activePhase.ErrorMessage}");
                }
            }

            if (showQuestion && f.NeedsUserInput && !string.IsNullOrEmpty(f.PlannerQuestion))
                sb.AppendLine($"  ? \"{f.PlannerQuestion}\"");
        }
    }

    /// <summary>
    /// Builds a prompt for generating plan discussion questions.
    /// Claude acts as a senior architect reviewing the plan with the team lead.
    /// </summary>
    public static (string systemPrompt, string userMessage) BuildPlanDiscussionPrompt(BacklogFeature feature)
    {
        var systemPrompt =
            """
            You are a Senior Architect reviewing an implementation plan with the team lead before development begins.

            Your goal is to generate 3-5 targeted questions that will improve the plan. Focus on:
            - **Implementation trade-offs**: Are there alternative approaches worth considering?
            - **User preferences**: UI layout choices, naming conventions, behavior details the user should decide
            - **Missing edge cases**: Error handling, boundary conditions, concurrent access
            - **Performance implications**: Could any part become a bottleneck? Are there caching or optimization decisions?
            - **Testing strategy**: What level of testing is appropriate? Integration vs unit tests?

            Each question must include 2-4 suggested answer options to help the user decide quickly.

            ## Output format
            Output ONLY a JSON block:
            ```json
            {
              "questions": [
                { "question": "...", "suggestedAnswers": ["Option A: ...", "Option B: ...", "Option C: ..."] }
              ]
            }
            ```

            ## Rules
            - Generate exactly 3-5 questions
            - Each question should be specific and actionable (not vague)
            - Suggested answers should represent distinct, reasonable choices
            - Don't ask questions that can be answered by reading the codebase — explore first
            - Base your questions on actual code exploration, not assumptions
            """;

        var sb = new StringBuilder();
        sb.AppendLine("Review this feature plan and generate discussion questions.");
        sb.AppendLine();
        sb.AppendLine($"## Feature: {feature.Title ?? "Untitled"}");
        sb.AppendLine($"## Original Idea");
        sb.AppendLine(feature.RawIdea);

        if (!string.IsNullOrEmpty(feature.UserContext))
        {
            sb.AppendLine();
            sb.AppendLine("## Additional User Context");
            sb.AppendLine(feature.UserContext);
        }

        if (feature.Phases.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Implementation Plan");
            foreach (var phase in feature.Phases)
            {
                sb.AppendLine($"### Phase {phase.Order}: {phase.Title}");
                sb.AppendLine(phase.Plan);
                if (!string.IsNullOrEmpty(phase.AcceptanceCriteria))
                    sb.AppendLine($"Acceptance: {phase.AcceptanceCriteria}");
                sb.AppendLine();
            }
        }

        return (systemPrompt, sb.ToString());
    }

    /// <summary>
    /// Builds a prompt for refining a plan based on user's discussion answers.
    /// Output format matches PlannerSystemPrompt (title + phases JSON).
    /// </summary>
    public static (string systemPrompt, string userMessage) BuildPlanRefinementPrompt(
        BacklogFeature feature, List<(string question, string answer)> answers)
    {
        var systemPrompt =
            """
            You are a Feature Planner revising an implementation plan based on the team lead's feedback.

            You have been given the original plan and the user's answers to discussion questions.
            Revise the plan to incorporate their decisions.

            ## Output format
            Output the revised plan as a JSON block (and nothing else after it):
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
            - Incorporate the user's answers into the revised plan
            - Keep the same phase structure unless the answers require restructuring
            - Don't make phases too small (combine related changes)
            - Don't make phases too large (>500 lines of code = split it)
            - Respect the existing project architecture and conventions
            """;

        var sb = new StringBuilder();
        sb.AppendLine("Revise this plan based on the team lead's feedback.");
        sb.AppendLine();
        sb.AppendLine($"## Feature: {feature.Title ?? "Untitled"}");
        sb.AppendLine($"## Original Idea");
        sb.AppendLine(feature.RawIdea);

        if (!string.IsNullOrEmpty(feature.UserContext))
        {
            sb.AppendLine();
            sb.AppendLine("## Additional User Context");
            sb.AppendLine(feature.UserContext);
        }

        if (feature.Phases.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Current Plan");
            foreach (var phase in feature.Phases)
            {
                sb.AppendLine($"### Phase {phase.Order}: {phase.Title}");
                sb.AppendLine(phase.Plan);
                if (!string.IsNullOrEmpty(phase.AcceptanceCriteria))
                    sb.AppendLine($"Acceptance: {phase.AcceptanceCriteria}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Discussion Answers");
        for (var i = 0; i < answers.Count; i++)
        {
            sb.AppendLine($"**Q{i + 1}:** {answers[i].question}");
            sb.AppendLine($"**A:** {answers[i].answer}");
            sb.AppendLine();
        }

        return (systemPrompt, sb.ToString());
    }
}
