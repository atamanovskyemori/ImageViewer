using System.Text;

namespace ImageViewer.Settings;

/// <summary>
/// シンプルな INI ファイルの読み書き（UTF-8）。
/// <list type="bullet">
/// <item>[セクション] と キー=値 の形式。セクション名・キー名は大文字小文字を区別しない</item>
/// <item>行頭が ; か # の行はコメントとして読み飛ばす（書き出し時には残らない）</item>
/// <item>同じキーが何度も出てきたら、最後のものを使う</item>
/// <item>書き出しは、最初に出てきた（または Set した）順番を保つ</item>
/// </list>
/// Windows の GetPrivateProfileString は文字コードの扱いが面倒（ANSI / UTF-16）なので使わない。
/// </summary>
public sealed class IniFile
{
    private readonly List<Section> _sections = [];

    /// <summary>INI の文字列を解析する。解釈できない行は無視する。</summary>
    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        Section? current = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                current = ini.GetOrAddSection(line[1..^1].Trim());
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue; // キーが無い行は無視
            }

            current ??= ini.GetOrAddSection(string.Empty);
            current.Set(line[..eq].Trim(), line[(eq + 1)..].Trim());
        }

        return ini;
    }

    /// <summary>値を取得する。無ければ null。</summary>
    public string? Get(string section, string key) =>
        FindSection(section)?.Get(key);

    /// <summary>値を設定する（無ければ追加）。改行は値に入れられないので空白に置き換える。</summary>
    public void Set(string section, string key, string value)
    {
        var safeValue = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        GetOrAddSection(section).Set(key, safeValue);
    }

    /// <summary>INI 形式の文字列にする（改行は CRLF）</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var section in _sections)
        {
            if (section.Name.Length > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append("\r\n");
                }
                sb.Append('[').Append(section.Name).Append("]\r\n");
            }

            foreach (var (key, value) in section.Items)
            {
                sb.Append(key).Append('=').Append(value).Append("\r\n");
            }
        }
        return sb.ToString();
    }

    private Section? FindSection(string name) =>
        _sections.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private Section GetOrAddSection(string name)
    {
        var section = FindSection(name);
        if (section is null)
        {
            section = new Section(name);

            // セクション名の無い項目は、必ず先頭に書き出す（後ろに置くと直前のセクションに入ってしまう）
            if (name.Length == 0)
            {
                _sections.Insert(0, section);
            }
            else
            {
                _sections.Add(section);
            }
        }
        return section;
    }

    private sealed class Section(string name)
    {
        private readonly List<(string Key, string Value)> _items = [];

        public string Name { get; } = name;

        public IReadOnlyList<(string Key, string Value)> Items => _items;

        public string? Get(string key)
        {
            int index = IndexOf(key);
            return index >= 0 ? _items[index].Value : null;
        }

        public void Set(string key, string value)
        {
            int index = IndexOf(key);
            if (index >= 0)
            {
                _items[index] = (_items[index].Key, value);
            }
            else
            {
                _items.Add((key, value));
            }
        }

        private int IndexOf(string key) =>
            _items.FindIndex(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
    }
}
