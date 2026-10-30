using System.Globalization;
using ReapDeep.Analytics;

namespace ReapDeep.QueryApi.Analytics;

public sealed class TrendFormula
{
    public const int MaxLength = 200;

    abstract record Node;
    sealed record Number(double Value) : Node;
    sealed record SeriesRef(int Index) : Node;
    sealed record Negate(Node Operand) : Node;
    sealed record Binary(char Op, Node Left, Node Right) : Node;

    readonly Node _root;
    public string Text { get; }

    TrendFormula(string text, Node root)
    {
        Text = text;
        _root = root;
    }

    public static TrendFormula Parse(string formula, int seriesCount)
    {
        var text = formula.Trim();
        Ensure.That(text.Length is > 0 and <= MaxLength, $"formula must be 1-{MaxLength} characters");
        var parser = new Parser(text.ToUpperInvariant(), seriesCount);
        var root = parser.Expression();
        parser.End();
        return new TrendFormula(text, root);
    }

    public double Evaluate(IReadOnlyList<double> values)
    {
        var v = Eval(_root, values);
        return double.IsFinite(v) ? Math.Round(v, 4) : 0;
    }

    static double Eval(Node node, IReadOnlyList<double> values) => node switch
    {
        Number n => n.Value,
        SeriesRef r => values[r.Index],
        Negate n => -Eval(n.Operand, values),
        Binary { Op: '+' } b => Eval(b.Left, values) + Eval(b.Right, values),
        Binary { Op: '-' } b => Eval(b.Left, values) - Eval(b.Right, values),
        Binary { Op: '*' } b => Eval(b.Left, values) * Eval(b.Right, values),
        Binary b => Eval(b.Right, values) is var d && d == 0 ? 0 : Eval(b.Left, values) / d,
        _ => 0,
    };

    sealed class Parser(string s, int seriesCount)
    {
        int _pos;

        public Node Expression()
        {
            var left = Term();
            while (Peek() is '+' or '-') left = new Binary(Next(), left, Term());
            return left;
        }

        Node Term()
        {
            var left = Factor();
            while (Peek() is '*' or '/') left = new Binary(Next(), left, Factor());
            return left;
        }

        Node Factor()
        {
            var c = Peek();
            if (c == '-')
            {
                Next();
                return new Negate(Factor());
            }
            if (c == '(')
            {
                Next();
                var inner = Expression();
                Ensure.That(Peek() == ')', "formula: missing ')'");
                Next();
                return inner;
            }
            if (c is >= 'A' and <= 'Z')
            {
                Next();
                var index = c - 'A';
                Ensure.That(index < seriesCount, $"formula: series {c} does not exist (use A-{(char)('A' + seriesCount - 1)})");
                return new SeriesRef(index);
            }
            if (char.IsAsciiDigit(c) || c == '.')
            {
                var start = _pos;
                while (_pos < s.Length && (char.IsAsciiDigit(s[_pos]) || s[_pos] == '.')) _pos++;
                Ensure.That(double.TryParse(s[start.._pos], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n),
                    $"formula: invalid number '{s[start.._pos]}'");
                return new Number(n);
            }
            throw new ValidationException(c == '\0' ? "formula: unexpected end" : $"formula: unexpected '{c}'");
        }

        public void End() => Ensure.That(Peek() == '\0', $"formula: unexpected '{Peek()}'");

        char Peek()
        {
            while (_pos < s.Length && s[_pos] == ' ') _pos++;
            return _pos < s.Length ? s[_pos] : '\0';
        }

        char Next()
        {
            var c = Peek();
            _pos++;
            return c;
        }
    }
}
