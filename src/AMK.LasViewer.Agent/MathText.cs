using System.Text;
using System.Text.RegularExpressions;

namespace AMK.LasViewer.Agent;

/// <summary>
/// Формулы LaTeX в ответе модели — в обычный текст с символами Unicode:
/// <c>$\rho_{\text{зп}} &lt; \rho_{\text{п}}$</c> → «ρзп &lt; ρп», <c>$0,2^\circ$</c> → «0,2°».
/// Панель агента LaTeX не рисует, а модели пишут его даже при запрете.
/// </summary>
public static partial class MathText
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε", ["varepsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ",
        ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["rho"] = "ρ", ["varrho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ",
        ["phi"] = "φ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Pi"] = "Π", ["Sigma"] = "Σ",
        ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["cdot"] = "·", ["times"] = "×", ["div"] = "÷", ["pm"] = "±", ["mp"] = "∓",
        ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥", ["ne"] = "≠", ["neq"] = "≠",
        ["approx"] = "≈", ["sim"] = "~", ["simeq"] = "≃", ["equiv"] = "≡", ["propto"] = "∝",
        ["lt"] = "<", ["gt"] = ">", ["ll"] = "≪", ["gg"] = "≫",
        ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["Rightarrow"] = "⇒", ["Leftrightarrow"] = "⇔",
        ["uparrow"] = "↑", ["downarrow"] = "↓",
        ["infty"] = "∞", ["circ"] = "°", ["degree"] = "°", ["partial"] = "∂", ["nabla"] = "∇",
        ["sum"] = "Σ", ["prod"] = "Π", ["int"] = "∫", ["ldots"] = "…", ["dots"] = "…", ["cdots"] = "…",
        ["percent"] = "%", ["%"] = "%", ["{"] = "{", ["}"] = "}", ["_"] = "_", ["&"] = "&", ["#"] = "#", ["$"] = "$",
        ["lg"] = "lg", ["ln"] = "ln", ["log"] = "log", ["exp"] = "exp", ["min"] = "min", ["max"] = "max",
        [","] = " ", [";"] = " ", [":"] = " ", ["!"] = "", ["quad"] = " ", ["qquad"] = " ", [" "] = " ",
        ["left"] = "", ["right"] = "", ["big"] = "", ["Big"] = "", ["displaystyle"] = "",
    };

    private const string SuperscriptSource = "0123456789+-−=()n";
    private const string SuperscriptTarget = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁻⁼⁽⁾ⁿ";
    private const string SubscriptSource = "0123456789+-−=()";
    private const string SubscriptTarget = "₀₁₂₃₄₅₆₇₈₉₊₋₋₌₍₎";

    /// <summary>Строка ответа с формулами в <c>$…$</c>, <c>$$…$$</c>, <c>\(…\)</c>, <c>\[…\]</c> — без LaTeX.</summary>
    public static string ToPlain(string text)
    {
        if (string.IsNullOrEmpty(text) || (text.IndexOf('$') < 0 && text.IndexOf('\\') < 0))
        {
            return text;
        }

        string result = Delimited().Replace(text, match =>
        {
            if (match.Groups["paren"].Success || match.Groups["bracket"].Success)
            {
                // «\(…\)» бывает и экранированными скобками Markdown: формула — только с командами или индексами.
                string inner = match.Groups["paren"].Success ? match.Groups["paren"].Value : match.Groups["bracket"].Value;
                return inner.IndexOfAny(['\\', '^', '_']) >= 0 ? Convert(inner) : match.Value;
            }

            return Convert(match.Groups["block"].Success ? match.Groups["block"].Value : match.Groups["inline"].Value);
        });

        // Команды вне разделителей (модель забыла «$»): только известные, чтобы
        // не тронуть пути Windows и экранирование Markdown.
        return BareCommand().Replace(result, match => Convert(match.Value));
    }

    /// <summary>Содержимое формулы — в текст.</summary>
    public static string Convert(string formula)
    {
        string s = formula;

        // \text{…}, \mathrm{…} и подобные — их содержимое как есть.
        for (int guard = 0; guard < 8; guard++)
        {
            string next = TextCommand().Replace(s, match => match.Groups[1].Value);
            if (next == s)
            {
                break;
            }

            s = next;
        }

        s = Fraction().Replace(s, match => $"{Group(match.Groups[1].Value)}/{Group(match.Groups[2].Value)}");
        s = SquareRoot().Replace(s, match => "√" + Group(match.Groups[1].Value));
        s = Degree().Replace(s, "°");
        s = Command().Replace(s, match =>
            Symbols.TryGetValue(match.Groups[1].Value, out string? symbol) ? symbol : match.Groups[1].Value);
        s = Superscript().Replace(s, match => Script(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, superscript: true));
        s = Subscript().Replace(s, match => Script(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, superscript: false));
        s = s.Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);
        s = Spaces().Replace(s, " ").Trim();
        // «0,2 °» → «0,2°»: так градусы пишутся в тексте.
        return s.Replace(" °", "°", StringComparison.Ordinal);
    }

    private static string Group(string value)
    {
        string plain = Convert(value);
        return plain.Length > 1 && plain.Any(c => " +-−·×/".Contains(c)) ? $"({plain})" : plain;
    }

    /// <summary>Индексы: цифры — надстрочными и подстрочными знаками, буквы — следом за символом (ρзп, ρп).</summary>
    private static string Script(string value, bool superscript)
    {
        string plain = Convert(value);
        string source = superscript ? SuperscriptSource : SubscriptSource;
        string target = superscript ? SuperscriptTarget : SubscriptTarget;
        if (plain.Length > 0 && plain.All(c => source.Contains(c)))
        {
            StringBuilder builder = new(plain.Length);
            foreach (char c in plain)
            {
                builder.Append(target[source.IndexOf(c)]);
            }

            return builder.ToString();
        }

        return superscript ? "^" + (plain.Length > 1 ? $"({plain})" : plain) : plain;
    }

    [GeneratedRegex(@"\$\$(?<block>.+?)\$\$|\$(?<inline>[^\s$](?:[^$\n]*?[^\s$])?)\$|\\\((?<paren>.+?)\\\)|\\\[(?<bracket>.+?)\\\]", RegexOptions.Singleline)]
    private static partial Regex Delimited();

    [GeneratedRegex(@"\\(?:rho|varrho|Delta|delta|varphi|phi|sigma|alpha|beta|gamma|lambda|mu|omega|Omega|pi|theta|eta|varepsilon|epsilon|circ|degree|le|leq|ge|geq|approx|cdot|times|pm|neq|ne|to|rightarrow|infty)(?![A-Za-z])(?:_\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}|_[A-Za-zА-Яа-я0-9])?")]
    private static partial Regex BareCommand();

    [GeneratedRegex(@"\\(?:text|textrm|textit|textbf|mathrm|mathit|mathbf|mathsf|operatorname|mbox)\s*\{([^{}]*)\}")]
    private static partial Regex TextCommand();

    [GeneratedRegex(@"\\[dt]?frac\s*\{((?:[^{}]|\{[^{}]*\})*)\}\s*\{((?:[^{}]|\{[^{}]*\})*)\}")]
    private static partial Regex Fraction();

    [GeneratedRegex(@"\\sqrt\s*\{((?:[^{}]|\{[^{}]*\})*)\}")]
    private static partial Regex SquareRoot();

    [GeneratedRegex(@"\^\s*\{?\s*\\circ\s*\}?")]
    private static partial Regex Degree();

    [GeneratedRegex(@"\\([A-Za-z]+|[,;:! %{}_&#$])")]
    private static partial Regex Command();

    [GeneratedRegex(@"\^\{([^{}]*)\}|\^(.)")]
    private static partial Regex Superscript();

    [GeneratedRegex(@"_\{([^{}]*)\}|_(.)")]
    private static partial Regex Subscript();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();
}
