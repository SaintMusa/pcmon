using System.Globalization;

namespace PcMon;

// Formats values straight into a span, so drawing a table creates no strings
internal static class Fmt
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Str(Span<char> d, string s)
    {
        int n = Math.Min(d.Length, s.Length);
        s.AsSpan(0, n).CopyTo(d);
        return n;
    }

    public static int Int(Span<char> d, long v) => v.TryFormat(d, out int n, default, Inv) ? n : 0;

    public static int Pct(Span<char> d, double v) => v < 0.05 ? Str(d, "-") : v.TryFormat(d, out int n, "F1", Inv) ? n : 0;

    public static int Rate(Span<char> d, double v) => v < 0.5 ? Str(d, "-") : v.TryFormat(d, out int n, "F0", Inv) ? n : 0;

    public static int Bytes(Span<char> d, double v)
    {
        if (v < 1) return Str(d, "-");
        string unit;
        if (v >= 1L << 40) { v /= 1L << 40; unit = " TB"; }
        else if (v >= 1 << 30) { v /= 1 << 30; unit = " GB"; }
        else if (v >= 1 << 20) { v /= 1 << 20; unit = " MB"; }
        else if (v >= 1 << 10) { v /= 1 << 10; unit = " KB"; }
        else unit = " B";
        if (!v.TryFormat(d, out int n, v >= 100 || unit == " B" ? "F0" : "F1", Inv)) return 0;
        return n + Str(d[n..], unit);
    }

    // A process name, indented to show its place in the tree view
    public static int Tree(Span<char> d, Proc p)
    {
        int n = 0;
        if (p.Depth > 0)
        {
            int indent = Math.Min(2 * (p.Depth - 1), Math.Max(0, d.Length - 2));
            d[..indent].Fill(' ');
            n = indent;
            d[n++] = '\u2514';
            d[n++] = ' ';
        }
        return n + Str(d[n..], p.Name);
    }

    public static string Size(double v)
    {
        Span<char> s = stackalloc char[24];
        return new string(s[..Bytes(s, v)]);
    }
}

internal delegate int CellFn<T>(T item, Span<char> destination);

internal sealed record Col<T>(string Title, int Width, bool Right, CellFn<T> Write, Comparison<T> Compare, Func<T, byte>? Color = null);

internal abstract class Panel
{
    public string Title = "";
    public string? Note;                     // shown instead of rows when the panel has no data
    public string? Info;                     // live text at the right of the title edge
    public int X, Y, W, H;
    public abstract int Count { get; }
    public abstract uint SelectedPid { get; }
    public abstract void Update();
    public abstract void Draw(Screen s, bool focused);
    public abstract void Move(int delta);
    public abstract void MoveTo(int index);
    public abstract void NextSort(int delta);
    public abstract void ReverseSort();
    public abstract bool Click(int x, int y);
    public bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;
}

internal sealed class Table<T> : Panel where T : class
{
    readonly Col<T>[] _cols;
    readonly Action<List<T>> _source;
    readonly Func<T, uint> _pid;
    readonly Func<T, bool>? _pinned;
    readonly Comparison<T> _tie;
    readonly Comparison<T> _compare;
    readonly List<T> _items = new(512);
    public Action<List<T>>? Reorder;      // optional last step after sorting (tree view)
    readonly int[] _widths;
    int _sel, _scroll, _sort;
    bool _desc, _chosen;
    T? _selected;

    public Table(string title, Col<T>[] cols, Action<List<T>> source, Func<T, uint> pid, int sort, bool desc, Comparison<T> tie, Func<T, bool>? pinned = null)
    {
        Title = title; _cols = cols; _source = source; _pid = pid; _sort = sort; _desc = desc; _tie = tie; _pinned = pinned;
        _widths = new int[cols.Length];
        _compare = Compare;      // one delegate, created once, rather than one per sort
    }

    int Compare(T a, T b)
    {
        if (_pinned != null)
        {
            bool pa = _pinned(a), pb = _pinned(b);
            if (pa != pb) return pa ? -1 : 1;
        }
        int c = _cols[_sort].Compare(a, b);
        if (c == 0) return _tie(a, b);       // a fixed tie-break stops rows from jumping around
        return _desc ? -c : c;
    }

    public override int Count => _items.Count;
    public override uint SelectedPid => _sel < _items.Count ? _pid(_items[_sel]) : 0;

    public override void Update()
    {
        _items.Clear();
        _source(_items);
        _items.Sort(_compare);
        Reorder?.Invoke(_items);

        // Once the user has picked an item, stay on it as sorting moves it around.
        // Before that, stay at the top so the busiest rows are the ones on screen.
        if (_chosen && _selected != null)
        {
            int index = _items.IndexOf(_selected);
            if (index >= 0) _sel = index;
        }
        else _sel = 0;
        _sel = Math.Clamp(_sel, 0, Math.Max(0, _items.Count - 1));
        _selected = _sel < _items.Count ? _items[_sel] : null;
    }

    public override void Move(int delta) => MoveTo(_sel + delta);

    public override void MoveTo(int index)
    {
        _sel = Math.Clamp(index, 0, Math.Max(0, _items.Count - 1));
        _chosen = _sel > 0;
        _selected = _sel < _items.Count ? _items[_sel] : null;
    }

    public override void NextSort(int delta)
    {
        _sort = (_sort + delta + _cols.Length) % _cols.Length;
        _desc = _cols[_sort].Right;           // numbers default to biggest first
        Update();
    }

    public override void ReverseSort() { _desc = !_desc; Update(); }

    void Layout()
    {
        int rest = W - 4 - (_cols.Length - 1), flex = 0;      // box edges plus a one-cell margin on each side
        for (int i = 0; i < _cols.Length; i++)
        {
            if (_cols[i].Width == 0) flex++;
            else { _widths[i] = _cols[i].Width; rest -= _cols[i].Width; }
        }
        for (int i = 0; i < _cols.Length; i++)
            if (_cols[i].Width == 0) _widths[i] = Math.Max(4, rest / flex);
    }

    public override void Draw(Screen s, bool focused)
    {
        if (H < 4) return;
        Layout();

        Span<char> title = stackalloc char[96];
        int titleLength = Fmt.Str(title, Title);
        titleLength += Fmt.Str(title[titleLength..], " (");
        titleLength += Fmt.Int(title[titleLength..], _items.Count);
        title[titleLength++] = ')';
        s.Frame(X, Y, W, H, title[..titleLength], focused ? Color.Accent : Color.Line, focused ? Color.Accent : Color.Text, Info);

        // Column headers
        int left = X + 2, limit = X + W - 2;
        s.Fill(X + 1, Y + 1, W - 2, 1, ' ', Color.Text, Color.HeaderBg);
        int x = left;
        for (int c = 0; c < _cols.Length && x < limit; c++)
        {
            Col<T> col = _cols[c];
            int width = Math.Min(_widths[c], limit - x);
            int len = Math.Min(col.Title.Length, width);
            int start = col.Right ? x + width - len : x;
            s.Text(start, Y + 1, col.Title, c == _sort ? Color.Yellow : Color.Text, Color.HeaderBg, c == _sort ? (byte)1 : (byte)0, len);
            if (c == _sort) s.Put(col.Right ? start - 1 : start + len, Y + 1, (char)(_desc ? 0x2193 : 0x2191), Color.Yellow, Color.HeaderBg);
            x += _widths[c] + 1;
        }

        // Rows
        int rows = H - 3;
        if (_items.Count == 0 && Note != null)
        {
            s.Text(left, Y + 2, Note, Color.Dim, 0, 0, W - 4);
            return;
        }
        if (_sel < _scroll) _scroll = _sel;
        if (_sel >= _scroll + rows) _scroll = _sel - rows + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _items.Count - rows));

        Span<char> cell = stackalloc char[512];
        for (int r = 0; r < rows && r + _scroll < _items.Count; r++)
        {
            int index = r + _scroll;
            T item = _items[index];
            int y = Y + 2 + r;
            bool selected = index == _sel && focused;
            byte bg = selected ? Color.SelectBg : (byte)0;
            bool pinned = _pinned != null && _pinned(item);
            if (selected) s.Fill(X + 1, y, W - 2, 1, ' ', Color.Text, bg);

            x = left;
            for (int c = 0; c < _cols.Length && x < limit; c++)
            {
                Col<T> col = _cols[c];
                int width = Math.Min(_widths[c], limit - x);
                int len = Math.Min(col.Write(item, cell), width);
                int start = col.Right ? x + width - len : x;
                byte fg = selected ? Color.White : pinned ? Color.Yellow : col.Color?.Invoke(item) ?? Color.Text;
                s.Text(start, y, cell[..len], fg, bg);
                x += _widths[c] + 1;
            }
        }

        // The scrollbar sits on the right edge of the box
        if (_items.Count > rows)
        {
            int thumb = Math.Max(1, rows * rows / _items.Count);
            int top = (rows - thumb) * _scroll / Math.Max(1, _items.Count - rows);
            for (int r = top; r < top + thumb; r++)
                s.Put(X + W - 1, Y + 2 + r, (char)0x2588, focused ? Color.Accent : Color.Dim);
        }
    }

    public override bool Click(int x, int y)
    {
        if (y == Y + 1)
        {
            int left = X + 2;
            for (int c = 0; c < _cols.Length; c++)
            {
                if (x >= left && x < left + _widths[c])
                {
                    if (c == _sort) _desc = !_desc;
                    else { _sort = c; _desc = _cols[c].Right; }
                    Update();
                    return true;
                }
                left += _widths[c] + 1;
            }
            return false;
        }
        if (y >= Y + 2 && y < Y + H - 1 && _scroll + y - Y - 2 < _items.Count)
        {
            MoveTo(_scroll + y - Y - 2);
            return true;
        }
        return false;
    }
}
