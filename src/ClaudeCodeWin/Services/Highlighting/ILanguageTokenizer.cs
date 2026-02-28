namespace ClaudeCodeWin.Services.Highlighting;

public interface ILanguageTokenizer
{
    List<SyntaxToken> Tokenize(string text);
}
