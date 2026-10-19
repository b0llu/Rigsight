using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rigsight.LangTool;

/// <summary>Where a text is shown: a source file and line.</summary>
public sealed record TextSite(string File, int Line);

/// <summary>
/// Finds every text the app can show in another language, by its English key, the way <see cref="Rigsight.Core.Lang"/>
/// looks it up: <c>Lang.T("...")</c>, <c>Lang.T($"...")</c> (holes numbered in order), <c>Lang.F("...", ...)</c> and
/// <c>Lang.Key("...")</c> in code, <c>{ctl:T ...}</c> in pages.
/// </summary>
public static partial class TextScanner
{
    public sealed class Result
    {
        /// <summary>Every key, in the order found (file by file), with where it's used.</summary>
        public Dictionary<string, List<TextSite>> Texts { get; } = new(StringComparer.Ordinal);

        /// <summary>Lookups whose text isn't written at the call, so it can't be listed (it must be listed elsewhere).</summary>
        public List<TextSite> NotWritten { get; } = [];

        internal void Add(string key, TextSite site)
        {
            if (!Texts.TryGetValue(key, out var sites)) Texts[key] = sites = [];
            sites.Add(site);
        }
    }

    /// <summary>Scans the programs under <paramref name="srcDir"/> (the repo's src folder).</summary>
    public static Result Scan(string srcDir)
    {
        var result = new Result();
        var files = Directory.EnumerateFiles(srcDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin"))
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string relative = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
            string text = File.ReadAllText(file);
            if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) ScanXaml(text, relative, result);
            else ScanCode(text, relative, result);
        }
        return result;
    }

    public static void ScanCode(string code, string file, Result result)
    {
        var root = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "T" or "F" or "Key" or "C" } member) continue;
            if (Rightmost(member.Expression) != "Lang") continue;
            var site = new TextSite(file, call.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            var args = call.ArgumentList.Arguments;
            string? context = null;
            if (member.Name.Identifier.ValueText == "C")
            {
                // Lang.C("context", text): listed as "‹context›text".
                if (args.Count < 2 || args[0].Expression is not LiteralExpressionSyntax ctx) { result.NotWritten.Add(site); continue; }
                context = ctx.Token.ValueText;
                args = args.RemoveAt(0);
            }
            var arg = args.FirstOrDefault()?.Expression;
            switch (arg)
            {
                case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                    result.Add(WithContext(context, lit.Token.ValueText), site);
                    break;
                case InterpolatedStringExpressionSyntax interpolated when member.Name.Identifier.ValueText is "T" or "C":
                    result.Add(WithContext(context, Template(interpolated)), site);
                    break;
                default:
                    result.NotWritten.Add(site);
                    break;
            }
        }
    }

    private static string WithContext(string? context, string key) => context is null ? key : Rigsight.Core.Lang.Context(context, key);

    private static string? Rightmost(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The key of an interpolated text, as <see cref="Rigsight.Core.LangText"/> builds it at run time.</summary>
    public static string Template(InterpolatedStringExpressionSyntax s)
    {
        var sb = new StringBuilder();
        int n = 0;
        // A raw string ($"""...""") holds its braces as they are; a regular one keeps them doubled in the token's text.
        bool raw = s.StringStartToken.IsKind(SyntaxKind.InterpolatedMultiLineRawStringStartToken)
                   || s.StringStartToken.IsKind(SyntaxKind.InterpolatedSingleLineRawStringStartToken);
        foreach (var part in s.Contents)
        {
            if (part is InterpolatedStringTextSyntax text)
            {
                string literal = text.TextToken.ValueText;
                if (!raw) literal = literal.Replace("{{", "{").Replace("}}", "}");
                sb.Append(literal.Replace("{", "{{").Replace("}", "}}"));
                continue;
            }
            var hole = (InterpolationSyntax)part;
            sb.Append('{').Append(n++);
            if (hole.AlignmentClause is { } align) sb.Append(',').Append(align.Value.ToString().Replace(" ", ""));
            if (hole.FormatClause is { } format) sb.Append(':').Append(format.FormatStringToken.ValueText);
            sb.Append('}');
        }
        return sb.ToString();
    }

    [GeneratedRegex("""\{ctl:T\s+(?:Text\s*=\s*)?(?:'((?:[^'\\]|\\.)*)'|((?:[^}\\]|\\.)*?))\s*\}""")]
    private static partial Regex XamlT();

    public static void ScanXaml(string xaml, string file, Result result)
    {
        foreach (Match m in XamlT().Matches(xaml))
        {
            string raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            // The attribute's XML escapes first (&amp;), then the markup extension's own (\, \' \{).
            string key = Regex.Replace(WebUtility.HtmlDecode(raw), @"\\(.)", "$1");
            int line = xaml.AsSpan(0, m.Index).Count('\n') + 1;
            result.Add(key, new TextSite(file, line));
        }
    }
}
