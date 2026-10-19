using System.Globalization;

namespace ImageViewer.Metadata;

/// <summary>
/// EXIF の値を表示用の文字列にする。おかしな値（分母 0、負のシャッター速度など）は null を返し、その行は出さない。
/// 単位はメートル法（mm / m）。
/// </summary>
public static class ExifFormat
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>シャッター速度。1秒未満は「1/250 秒」、1秒以上は「2.5 秒」</summary>
    public static string? ExposureTime(Rational? value)
    {
        if (value is not { IsValid: true } r || r.Numerator <= 0 || r.Denominator <= 0)
        {
            return null;
        }

        double seconds = r.ToDouble();
        if (seconds >= 1)
        {
            return $"{Number(seconds, "0.#")} 秒";
        }

        // 10/2500 のように約分されていない値もあるので、分母をそのまま使わず計算する
        long denominator = r.Numerator == 1 ? r.Denominator : (long)Math.Round(1 / seconds);
        return $"1/{denominator} 秒";
    }

    /// <summary>F値（「F2.8」）</summary>
    public static string? FNumber(Rational? value) =>
        Positive(value) is { } f ? $"F{Number(f, "0.#")}" : null;

    /// <summary>焦点距離（「50 mm」。35mm 換算があれば「50 mm（35mm換算 75 mm）」）</summary>
    public static string? FocalLength(Rational? value, long? equivalent35mm)
    {
        var focal = Positive(value);
        string? equivalent = equivalent35mm is > 0 ? $"35mm換算 {equivalent35mm} mm" : null;

        return (focal, equivalent) switch
        {
            ({ } f, { } e) => $"{Number(f, "0.#")} mm（{e}）",
            ({ } f, null) => $"{Number(f, "0.#")} mm",
            (null, { } e) => e,
            _ => null,
        };
    }

    /// <summary>露出補正（「+0.7 EV」「-1 EV」「0 EV」）</summary>
    public static string? ExposureBias(Rational? value)
    {
        if (value is not { IsValid: true } r)
        {
            return null;
        }

        double ev = Math.Round(r.ToDouble(), 1);
        if (ev == 0)
        {
            return "0 EV";
        }
        return $"{(ev > 0 ? "+" : "")}{Number(ev, "0.#")} EV";
    }

    public static string? Iso(long? value) => value is > 0 ? $"ISO {value}" : null;

    /// <summary>フラッシュ。ビット0 が発光、ビット5 が「フラッシュ機能なし」</summary>
    public static string? Flash(long? value) => value switch
    {
        null => null,
        { } v when (v & 0x01) != 0 => "発光",
        { } v when (v & 0x20) != 0 => "機能なし",
        _ => "発光なし",
    };

    public static string? ExposureProgram(long? value) => value switch
    {
        1 => "マニュアル",
        2 => "プログラムAE",
        3 => "絞り優先",
        4 => "シャッター優先",
        5 => "クリエイティブ",
        6 => "アクション",
        7 => "ポートレート",
        8 => "風景",
        _ => null, // 0（未定義）や知らない値は出さない
    };

    public static string? MeteringMode(long? value) => value switch
    {
        1 => "平均",
        2 => "中央重点",
        3 => "スポット",
        4 => "マルチスポット",
        5 => "分割測光",
        6 => "部分測光",
        255 => "その他",
        _ => null,
    };

    public static string? WhiteBalance(long? value) => value switch
    {
        0 => "オート",
        1 => "マニュアル",
        _ => null,
    };

    /// <summary>向き。「正しく見るには、どう回す / 反転するか」を書く（WicImageDecoder がやっている変換と同じ）</summary>
    public static string? Orientation(long? value) => value switch
    {
        1 => "通常",
        2 => "左右反転",
        3 => "180°回転",
        4 => "上下反転",
        5 => "右90°回転＋左右反転",
        6 => "右90°回転",
        7 => "左90°回転＋左右反転",
        8 => "左90°回転",
        _ => null,
    };

    /// <summary>
    /// EXIF の日時（"2024:05:01 12:34:56"）を「2024/05/01 12:34:56」にする。
    /// タイムゾーン（"+09:00"）があれば後ろに付ける。日時として読めなければ、そのまま出す。
    /// </summary>
    public static string? DateTime(string? value, string? offset = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        // 日時が分からないカメラは 0 や空白で埋めてくる
        if (value.All(c => c is '0' or ':' or ' '))
        {
            return null;
        }

        string text = System.DateTime.TryParseExact(
                value, "yyyy:MM:dd HH:mm:ss", Invariant, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy/MM/dd HH:mm:ss", Invariant)
            : value;

        return string.IsNullOrWhiteSpace(offset) ? text : $"{text}（{offset.Trim()}）";
    }

    /// <summary>
    /// 緯度・経度（度・分・秒の3つの有理数）を「35°39'31.2"N（35.658667）」にする。
    /// reference は "N" / "S" / "E" / "W"。南緯・西経は、10進の値を負にする。
    /// </summary>
    public static string? GpsCoordinate(IReadOnlyList<Rational>? dms, string? reference)
    {
        if (dms is not { Count: >= 3 } || !dms[0].IsValid || !dms[1].IsValid || !dms[2].IsValid)
        {
            return null;
        }

        double degrees = dms[0].ToDouble();
        double minutes = dms[1].ToDouble();
        double seconds = dms[2].ToDouble();
        if (degrees < 0 || minutes < 0 || seconds < 0)
        {
            return null;
        }

        double decimalDegrees = degrees + minutes / 60 + seconds / 3600;
        if (decimalDegrees > 180)
        {
            return null;
        }

        string direction = reference?.Trim().ToUpperInvariant() ?? string.Empty;
        if (direction is "S" or "W")
        {
            decimalDegrees = -decimalDegrees;
        }

        // 分に端数が入っている書き方（35° 39.52' 0"）もあるので、10進から度分秒を作り直す
        double absolute = Math.Abs(decimalDegrees);
        int d = (int)absolute;
        double totalMinutes = (absolute - d) * 60;
        int m = (int)totalMinutes;
        double s = Math.Round((totalMinutes - m) * 60, 1);
        if (s >= 60)
        {
            s -= 60;
            m++;
        }
        if (m >= 60)
        {
            m -= 60;
            d++;
        }

        return $"{d}°{m:00}'{Number(s, "00.0")}\"{direction}（{Number(decimalDegrees, "0.000000")}）";
    }

    /// <summary>高度（「40.1 m」）。reference が 1 なら海面下</summary>
    public static string? Altitude(Rational? value, long? reference)
    {
        if (value is not { IsValid: true } r || r.Denominator < 0)
        {
            return null;
        }

        double meters = r.ToDouble();
        if (reference == 1)
        {
            meters = -meters;
        }
        return $"{Number(meters, "0.#")} m";
    }

    /// <summary>ファイルサイズ（「3.2 MB（3,354,112 バイト）」）</summary>
    public static string FileSize(long bytes)
    {
        string exact = $"{bytes.ToString("N0", Invariant)} バイト";
        if (bytes < 1024)
        {
            return exact;
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = -1;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{Number(size, "0.#")} {units[unit]}（{exact}）";
    }

    private static double? Positive(Rational? value) =>
        value is { IsValid: true } r && r.ToDouble() > 0 ? r.ToDouble() : null;

    private static string Number(double value, string format) => value.ToString(format, Invariant);
}
