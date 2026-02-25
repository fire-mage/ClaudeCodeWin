namespace ClaudeCodeWin.Services;

/// <summary>
/// Markdown content for built-in marketplace plugins.
/// </summary>
internal static class BuiltInContent
{
    public const string CodeReviewChecklist = """
        # Code Review Checklist

        A systematic approach to reviewing code changes. Use this as a mental framework when reviewing PRs or your own code before committing.

        ## Correctness
        - Does the code do what it's supposed to do? Trace the logic manually.
        - Are edge cases handled (null, empty, boundary values, overflow)?
        - Are error conditions handled gracefully? No silent failures.
        - Are return values and exit conditions correct?

        ## Security
        - No hardcoded secrets, tokens, or passwords.
        - User input is validated and sanitized before use.
        - SQL queries use parameterized statements (no string concatenation).
        - File paths are validated to prevent path traversal.
        - Authentication/authorization checks are in place where needed.
        - No sensitive data in logs.

        ## Performance
        - No unnecessary database queries or API calls inside loops.
        - Large collections: consider pagination, streaming, or lazy loading.
        - Caching: is it appropriate here? Is cache invalidation handled?
        - No O(n^2) or worse algorithms where O(n log n) or O(n) is possible.
        - Async operations: are they truly needed? Are they awaited properly?

        ## Maintainability
        - Single responsibility: each function/class does one thing.
        - No magic numbers — use named constants.
        - Method names clearly describe what they do.
        - Complex logic has brief comments explaining "why", not "what".
        - No dead code or commented-out blocks.
        - DRY: repeated code should be extracted if used 3+ times.

        ## Testing
        - Are new code paths covered by tests?
        - Do tests test behavior, not implementation details?
        - Are test names descriptive (given-when-then or similar)?
        - Negative test cases: invalid input, error paths, timeouts.

        ## How to use
        When asked to review code, go through each section above systematically. Report findings grouped by category with severity (critical / warning / suggestion).
        """;

    public const string GitWorkflow = """
        # Git Workflow & Conventions

        Best practices for git workflows, commit messages, and branch management.

        ## Conventional Commits
        Format: `<type>(<scope>): <description>`

        Types:
        - `feat` — new feature
        - `fix` — bug fix
        - `refactor` — code change that neither fixes a bug nor adds a feature
        - `docs` — documentation changes
        - `test` — adding or updating tests
        - `chore` — maintenance (deps, CI, build)
        - `perf` — performance improvement
        - `style` — formatting, whitespace (no code logic change)

        Examples:
        - `feat(auth): add OAuth2 login flow`
        - `fix(api): handle null response from payment gateway`
        - `refactor(ui): extract Button component from FormPanel`

        ## Branch Strategy
        - `main` — stable, deployable code
        - `feature/<name>` — new features, branch from main
        - `fix/<name>` — bug fixes
        - `release/<version>` — release preparation (optional)

        ## Commit Best Practices
        - Each commit should be a single logical change.
        - Write the commit message in imperative mood ("Add feature" not "Added feature").
        - First line: max 72 characters.
        - Body (optional): explain WHY, not WHAT.
        - Never commit secrets, large binaries, or build artifacts.

        ## PR Guidelines
        - Keep PRs small and focused (under 400 lines when possible).
        - Title: short summary of the change.
        - Body: explain what changed and why, link related issues.
        - Request review from relevant team members.
        - Address all review comments before merging.

        ## How to use
        When creating commits or PRs, follow these conventions. When reviewing, check that commits and branches follow this structure.
        """;

    public const string ApiSecurity = """
        # API Security Fundamentals

        Essential security practices for building and reviewing APIs. Based on OWASP API Security Top 10.

        ## Authentication
        - Use established standards (OAuth 2.0, OpenID Connect, JWT).
        - Never store passwords in plain text — use bcrypt, scrypt, or Argon2.
        - Implement rate limiting on login endpoints.
        - Use short-lived tokens with refresh token rotation.
        - Invalidate tokens server-side on logout.

        ## Authorization
        - Check permissions on every request, not just at the UI level.
        - Use RBAC (Role-Based Access Control) or ABAC (Attribute-Based).
        - Object-level authorization: user can only access their own resources (BOLA prevention).
        - Function-level authorization: admin endpoints must verify admin role.

        ## Input Validation
        - Validate all input: type, length, format, range.
        - Use allowlists over denylists when possible.
        - Parameterize database queries — never concatenate user input.
        - Sanitize output to prevent XSS (HTML-encode by default).
        - Validate Content-Type headers.

        ## Data Protection
        - Use HTTPS everywhere.
        - Don't expose sensitive data in URLs (use POST body or headers).
        - Minimize data in API responses — return only what's needed.
        - Mask or omit sensitive fields in logs.
        - Set proper CORS headers (don't use wildcard * in production).

        ## Error Handling
        - Don't expose stack traces or internal details in error responses.
        - Use generic error messages for clients, detailed logs server-side.
        - Return appropriate HTTP status codes.
        - Rate-limit error responses to prevent enumeration attacks.

        ## How to use
        When building or reviewing APIs, check each section. When doing a security review, use this as a checklist and report findings by severity.
        """;

    public const string PerformanceProfiling = """
        # Performance Profiling Guide

        How to identify, measure, and fix performance bottlenecks in applications.

        ## Profiling Mindset
        - NEVER optimize without measuring first.
        - Identify the bottleneck before fixing — the obvious guess is often wrong.
        - Measure → Change → Measure again. No measurement = no proof.

        ## Common Bottleneck Categories

        ### CPU-bound
        - Tight loops with heavy computation.
        - Regex over large texts (use compiled regex or simpler parsing).
        - JSON serialization/deserialization of large objects.
        - Repeated sorting, searching in unsorted collections.

        ### I/O-bound
        - Database queries: missing indexes, N+1 queries, full table scans.
        - Network calls: sequential when they could be parallel.
        - File system: reading large files entirely when streaming would work.
        - DNS lookups, connection establishment (reuse connections).

        ### Memory
        - Large object allocations in hot paths.
        - String concatenation in loops (use StringBuilder).
        - Holding references to large objects longer than needed.
        - Event handler leaks (subscribe but never unsubscribe).

        ## Optimization Patterns
        - **Caching**: Cache expensive computations, DB queries, API responses. Always plan invalidation.
        - **Batching**: Combine multiple small operations into one (bulk DB inserts, batch API calls).
        - **Lazy loading**: Don't load data until it's actually needed.
        - **Pagination**: Never load unbounded lists. Always paginate.
        - **Async I/O**: Use async/await for I/O to free threads.
        - **Connection pooling**: Reuse database and HTTP connections.

        ## How to use
        When asked to optimize or when you notice slow operations, apply this framework: identify the category, measure, apply the appropriate pattern, and verify improvement.
        """;

    public const string TestingStrategies = """
        # Testing Strategies

        Approaches to writing effective tests: TDD workflow, test types, and practical patterns.

        ## TDD Workflow
        1. **Red**: Write a failing test that describes the desired behavior.
        2. **Green**: Write the minimum code to make the test pass.
        3. **Refactor**: Clean up the code while keeping tests green.

        Benefits: tests serve as a contract, drive API design, catch regressions early.

        ## Test Types

        ### Unit Tests
        - Test a single function or class in isolation.
        - Mock external dependencies (DB, network, file system).
        - Fast (milliseconds), run on every change.
        - Cover: business logic, edge cases, error handling.

        ### Integration Tests
        - Test interaction between components (API + DB, service + external API).
        - Use real dependencies or realistic fakes (in-memory DB, test containers).
        - Slower but catch wiring and configuration issues.

        ### End-to-End Tests
        - Test full user workflows through the UI or API.
        - Slowest, most brittle — keep the number small.
        - Focus on critical happy paths.

        ## Test Design Patterns
        - **Arrange-Act-Assert (AAA)**: Separate setup, execution, and verification.
        - **One assertion per test**: Each test verifies one behavior (multiple asserts OK if checking one concept).
        - **Descriptive names**: `Should_ReturnEmpty_When_NoItemsMatch()` or `given_empty_cart_when_checkout_then_error`.
        - **Test behavior, not implementation**: If you refactor and tests break, the tests were too coupled.

        ## What NOT to Test
        - Framework/library internals (don't test that Entity Framework can save).
        - Trivial getters/setters with no logic.
        - Private methods directly — test through public API.
        - UI layout details (test behavior, not pixel positions).

        ## Mocking Guidelines
        - Mock only what you own (wrap third-party code, mock your wrapper).
        - Prefer fakes over mocks when logic is needed.
        - Don't mock everything — if it's easy to use the real thing, do it.

        ## How to use
        When starting new features, suggest writing tests first. When fixing bugs, write a failing test that reproduces the bug before fixing. When reviewing, check test coverage and quality.
        """;
}
