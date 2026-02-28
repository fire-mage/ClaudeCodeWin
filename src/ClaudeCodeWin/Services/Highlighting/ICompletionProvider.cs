using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services.Highlighting;

public enum CompletionTrigger
{
    Manual,  // Ctrl+Space
    Dot,     // After "."
    Typing,  // After 2+ identifier chars (debounced)
    Angle,   // After "<" (HTML tags)
}

public interface ICompletionProvider
{
    List<CompletionItem> GetCompletions(string text, int caretPosition, CompletionTrigger trigger, List<SyntaxToken> tokens);
    (int start, string prefix) GetWordAtCaret(string text, int caretPosition);

    /// <summary>Returns the trigger type for the character, or null if not a trigger. Replaces IsTriggerCharacter + hardcoded mapping.</summary>
    CompletionTrigger? GetTriggerForCharacter(char c) => c == '.' ? CompletionTrigger.Dot : null;

    /// <summary>Whether the given character is valid inside an identifier for this language (used for filtering).</summary>
    bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
