namespace ClaudeCodeWin.Services.Highlighting;

public enum SyntaxTokenType
{
    PlainText,
    Keyword,
    ControlKeyword,
    TypeKeyword,
    Literal,
    String,
    Number,
    Comment,
    Preprocessor,
    TypeName,
    Attribute,
    TagName,
}

public readonly record struct SyntaxToken(int Start, int Length, SyntaxTokenType Type);
