using System.Runtime.InteropServices;

namespace PcMon;

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct Cell
{
    public char Ch;
    public byte Fg, Bg;        // 256-color palette index, 0 = the terminal's default
    public byte Bold, Pad;

    public readonly bool Same(in Cell o) => Ch == o.Ch && Fg == o.Fg && Bg == o.Bg && Bold == o.Bold;
}

internal static class Color
{
    public const byte Default = 0, Text = 252, Dim = 245, Accent = 39, Green = 78, Yellow = 220, Orange = 208, Red = 203, Purple = 141,
        Blue = 75, BarBg = 236, HeaderBg = 237, SelectBg = 24, White = 255, Black = 16, Line = 240;
}

// A grid of cells kept in memory. Drawing only changes the grid. Flush then compares
// it with what is on screen and sends just the cells that differ, which is what
// removes the flicker and keeps the terminal itself from doing extra work.
internal sealed unsafe class Screen
{
    public int W { get; private set; }
    public int H { get; private set; }
    Cell[] _cur = [], _prev = [];
    char[] _out = new char[1 << 16];
    int _n;
    readonly nint _handle;
    public long CharsWritten;

    public Screen(nint handle) => _handle = handle;

    public void Resize(int w, int h)
    {
        if (w == W && h == H) return;
        W = w; H = h;
        _cur = new Cell[w * h];
        _prev = new Cell[w * h];
        Array.Fill(_prev, new Cell { Ch = '￿' });   // forces a full repaint
        Emit("\x1b[0m\x1b[2J");
    }

    // Forgets what is on screen, so the next Flush repaints everything. Needed when
    // something else has written to the terminal behind our back.
    public void Invalidate()
    {
        Array.Fill(_prev, new Cell { Ch = '\uFFFF' });
        Emit("\x1b[0m\x1b[2J");
    }

    public void Clear() => Array.Fill(_cur, new Cell { Ch = ' ' });

    public void Put(int x, int y, char c, byte fg = Color.Text, byte bg = 0, byte bold = 0)
    {
        if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
        // Keep double-width characters off the grid; arrows, box and block symbols are single width
        if (c >= 0x1100 && !(c >= 0x2190 && c <= 0x21FF) && !(c >= 0x2500 && c <= 0x25FF)) c = '?';
        ref Cell cell = ref _cur[y * W + x];
        cell.Ch = c < ' ' ? ' ' : c; cell.Fg = fg; cell.Bg = bg; cell.Bold = bold;
    }

    public int Text(int x, int y, ReadOnlySpan<char> s, byte fg = Color.Text, byte bg = 0, byte bold = 0, int max = int.MaxValue)
    {
        int n = Math.Min(s.Length, max);
        for (int i = 0; i < n; i++) Put(x + i, y, s[i], fg, bg, bold);
        return x + n;
    }

    public void Fill(int x, int y, int w, int h, char c = ' ', byte fg = Color.Text, byte bg = 0)
    {
        for (int r = 0; r < h; r++)
            for (int i = 0; i < w; i++) Put(x + i, y + r, c, fg, bg);
    }

    // A thin rounded box with a title set into the top edge and optional live text at the right
    public void Frame(int x, int y, int w, int h, ReadOnlySpan<char> title, byte border, byte titleColor,
                      ReadOnlySpan<char> info = default, byte infoColor = Color.Dim, byte bg = 0)
    {
        if (w < 4 || h < 2) return;
        int right = x + w - 1, bottom = y + h - 1;
        for (int i = x + 1; i < right; i++)
        {
            Put(i, y, (char)0x2500, border, bg);
            Put(i, bottom, (char)0x2500, border, bg);
        }
        for (int i = y + 1; i < bottom; i++)
        {
            Put(x, i, (char)0x2502, border, bg);
            Put(right, i, (char)0x2502, border, bg);
        }
        Put(x, y, (char)0x256D, border, bg);
        Put(right, y, (char)0x256E, border, bg);
        Put(x, bottom, (char)0x2570, border, bg);
        Put(right, bottom, (char)0x256F, border, bg);

        int titleEnd = x + 2;
        if (title.Length > 0)
        {
            Put(x + 2, y, ' ', titleColor, bg);
            titleEnd = Text(x + 3, y, title, titleColor, bg, 1, w - 6);
            Put(titleEnd++, y, ' ', titleColor, bg);
        }

        int room = right - titleEnd - 3;
        if (info.Length > 0 && room > 3)
        {
            int n = Math.Min(info.Length, room);
            int start = right - n - 2;
            Put(start - 1, y, ' ', infoColor, bg);
            Text(start, y, info[..n], infoColor, bg);
            Put(start + n, y, ' ', infoColor, bg);
        }
    }

    void Emit(ReadOnlySpan<char> s)
    {
        if (_n + s.Length > _out.Length) Array.Resize(ref _out, _out.Length * 2);
        s.CopyTo(_out.AsSpan(_n));
        _n += s.Length;
    }

    void EmitNumber(int value)
    {
        if (_n + 8 > _out.Length) Array.Resize(ref _out, _out.Length * 2);
        value.TryFormat(_out.AsSpan(_n), out int written);
        _n += written;
    }

    public void Flush()
    {
        byte fg = 255, bg = 255, bold = 255;
        bool first = true, any = false;
        for (int y = 0; y < H; y++)
        {
            int cursor = -1;
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                ref Cell c = ref _cur[i];
                if (c.Same(_prev[i])) continue;
                if (!any) { any = true; Emit("\x1b[?2026h"); }     // tells the terminal to show the update in one go
                if (cursor != x)
                {
                    Emit("\x1b["); EmitNumber(y + 1); Emit(";"); EmitNumber(x + 1); Emit("H");
                }
                if (first || c.Fg != fg || c.Bg != bg || c.Bold != bold)
                {
                    first = false; fg = c.Fg; bg = c.Bg; bold = c.Bold;
                    Emit("\x1b[0");
                    if (bold != 0) Emit(";1");
                    if (fg != 0) { Emit(";38;5;"); EmitNumber(fg); }
                    if (bg != 0) { Emit(";48;5;"); EmitNumber(bg); }
                    Emit("m");
                }
                if (_n >= _out.Length) Array.Resize(ref _out, _out.Length * 2);
                _out[_n++] = c.Ch;
                cursor = x + 1;
                _prev[i] = c;
            }
        }
        if (any) Emit("\x1b[?2026l");
        if (_n > 0)
        {
            fixed (char* p = _out)
            {
                uint written;
                Native.WriteConsoleW(_handle, p, (uint)_n, &written, null);
            }
            CharsWritten += _n;
            _n = 0;
        }
    }

    public string[] Lines()
    {
        var lines = new string[H];
        var chars = new char[W];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++) chars[x] = _cur[y * W + x].Ch;
            lines[y] = new string(chars).TrimEnd();
        }
        return lines;
    }
}
