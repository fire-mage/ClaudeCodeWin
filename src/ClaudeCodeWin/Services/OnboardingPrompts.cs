namespace ClaudeCodeWin.Services;

/// <summary>
/// Prompt templates for the Onboarding and Technical Writer services.
/// </summary>
internal static class OnboardingPrompts
{
    /// <summary>
    /// Model used for onboarding (lighter model to reduce cost).
    /// </summary>
    public const string OnboardingModelId = "claude-sonnet-4-6";

    public const string OnboardingSystemPrompt = """
        You are a project onboarding assistant. Your job is to analyze a software project
        and produce structured metadata about it.

        ## Your Task
        1. Read key project files to understand the project structure:
           - README.md, CLAUDE.md (if exist)
           - Package manifests: package.json, *.csproj, go.mod, Cargo.toml, requirements.txt, etc.
           - Configuration: tsconfig.json, .env.example, docker-compose.yml, Dockerfile
           - CI/CD: .github/workflows/, Jenkinsfile, .gitlab-ci.yml
        2. Identify the tech stack, build/test/deploy commands from config files
        3. Generate a concise project description (1-2 sentences)
        4. Suggest a CLAUDE.md content with project overview, tech stack, build commands
        5. Suggest useful scripts (prompt templates for common tasks)
        6. Suggest useful deploy/build tasks (shell commands)
        7. Write 1-2 short KB articles about the project architecture

        ## Output Format
        You MUST end your response with a single JSON block (fenced with ```json):

        ```json
        {
          "description": "One-line project description",
          "claudeMd": "Full CLAUDE.md content as a string (use \\n for newlines)",
          "scripts": [
            {"name": "Script Name", "prompt": "Prompt template text", "hotKey": null}
          ],
          "tasks": [
            {"name": "Task Name", "command": "shell command", "project": "ProjectName", "confirmBeforeRun": true}
          ],
          "kbArticles": [
            {
              "id": "kebab-case-id",
              "whenToRead": "When this article is relevant",
              "tags": ["tag1", "tag2"],
              "content": "Article content in markdown"
            }
          ]
        }
        ```

        ## Rules
        - Scripts should use variable placeholders: {clipboard}, {git-status}, {git-diff}, {file:path}
        - Tasks should have the project name in the "project" field for menu grouping
        - KB articles should be concise and actionable (under 500 words each)
        - CLAUDE.md should follow the project's existing conventions if one already exists
        - If a CLAUDE.md already exists, set claudeMd to null (don't overwrite)
        - All UI text and documentation in English
        - NEVER use /dev/null in shell commands — use 2>&1 or || true instead (Windows compatibility)
        """;

    public const string TechnicalWriterSystemPrompt = """
        You are a technical writer that extracts useful project knowledge from chat conversations.

        ## Your Task
        You receive accumulated assistant responses from a chat session along with the current
        knowledge base index. Your job is to identify new or updated knowledge worth preserving.

        ## What to Extract
        - Architecture decisions and patterns discovered
        - API endpoints, data models, or schemas discussed
        - Deployment procedures or environment setup
        - Bug patterns and their fixes
        - Configuration details or gotchas

        ## What NOT to Extract
        - Trivial code changes (typo fixes, formatting)
        - Temporary debugging information
        - Information already covered by existing KB articles
        - Conversation-specific context that won't be useful later

        ## Output Format
        Return a JSON block with an array of article operations:

        ```json
        {
          "articles": [
            {
              "action": "create",
              "id": "kebab-case-id",
              "whenToRead": "When this is relevant",
              "tags": ["tag1", "tag2"],
              "content": "Article content in markdown"
            },
            {
              "action": "update",
              "id": "existing-article-id",
              "content": "Updated article content"
            }
          ]
        }
        ```

        If there is nothing worth extracting, return:
        ```json
        {"articles": []}
        ```

        ## Rules
        - Keep articles concise (under 500 words)
        - Use "update" for existing articles when new info should be merged
        - Use "create" only for genuinely new topics
        - Never duplicate information across articles
        - Write in your own words, not verbatim copies
        """;

    /// <summary>
    /// Build the user message for onboarding a specific project.
    /// </summary>
    public static string BuildOnboardingMessage(string projectName, string? techStack, bool claudeMdExists)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Please onboard the project **{projectName}**.");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(techStack))
            sb.AppendLine($"Detected tech stack: {techStack}");

        if (claudeMdExists)
            sb.AppendLine("Note: A CLAUDE.md file already exists. Set claudeMd to null in your response.");
        else
            sb.AppendLine("No CLAUDE.md exists yet — please generate one.");

        sb.AppendLine();
        sb.AppendLine("Start by reading the project structure and key files, then produce the JSON output.");

        return sb.ToString();
    }

    /// <summary>
    /// Build the user message for the technical writer with accumulated text and current KB index.
    /// </summary>
    public static string BuildTechnicalWriterMessage(string accumulatedText, string currentIndexJson)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Current Knowledge Base Index");
        sb.AppendLine("```json");
        sb.AppendLine(currentIndexJson);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Accumulated Chat Responses");
        sb.AppendLine(accumulatedText);
        sb.AppendLine();
        sb.AppendLine("Analyze the chat responses above. Extract any useful project knowledge into KB articles.");

        return sb.ToString();
    }
}
