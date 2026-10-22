using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>
/// アニメーションのコマを出すもの。
/// <list type="bullet">
/// <item><see cref="FrameList"/>：全コマを合成済みでメモリに持つ（ふつうはこちら。速くて簡単）</item>
/// <item><see cref="StreamingFrames"/>：再生しながら、少し先までのコマだけを裏で作る（全コマを持つとメモリの上限を超えるとき）</item>
/// </list>
/// </summary>
public interface IAnimationFrames
{
    int Count { get; }

    /// <summary>各コマの表示時間（補正済み）</summary>
    IReadOnlyList<TimeSpan> Delays { get; }

    /// <summary>
    /// index のコマ。まだ用意できていなければ、その手前でいちばん近い用意できているコマ（何も無ければ null）。
    /// 作るのが追いつかないときは、コマを飛ばして見せる（止まって待たない）
    /// </summary>
    BitmapSource? Get(int index);

    /// <summary>index のコマと、その少し先までを用意する（裏で。すぐ戻る）</summary>
    void Prepare(int index);

    /// <summary>コマが用意できた（裏のスレッドで起きる）</summary>
    event EventHandler? FrameReady;
}

/// <summary>全コマを合成済みでメモリに持つ。どのコマもすぐ出せる</summary>
public sealed class FrameList : IAnimationFrames
{
    private readonly IReadOnlyList<AnimationFrame> _frames;

    public FrameList(IReadOnlyList<AnimationFrame> frames)
    {
        _frames = frames;
        Delays = frames.Select(f => f.Delay).ToList();
    }

    public int Count => _frames.Count;

    public IReadOnlyList<TimeSpan> Delays { get; }

    public BitmapSource? Get(int index) => index >= 0 && index < _frames.Count ? _frames[index].Image : null;

    public void Prepare(int index)
    {
        // 全部持っているので、用意するものは無い
    }

    // 全部持っているので、起きることは無い
    public event EventHandler? FrameReady
    {
        add { }
        remove { }
    }
}

/// <summary>
/// 再生しながらコマを作る（全コマを持つとメモリの上限を超える、大きい・コマ数の多いアニメーション）。
/// <para>
/// GIF もアニメーションWebP も、コマは前のコマに重ねて作るので、最初から順にしか作れない。
/// そこで裏で順に作り、今見せているコマの少し先（と少し手前）だけを持っておく。
/// </para>
/// <list type="bullet">
/// <item>持っておく枚数は、作るときに渡す（メモリの上限に収まる枚数）。先を多めに、手前（, で戻る用）を少し</item>
/// <item>持っていない手前のコマに戻るときは、最初から作り直す</item>
/// <item>作るのが再生に追いつかないときは、用意できているいちばん近いコマを見せる（遅いマシンではコマ落ちする）</item>
/// </list>
/// 作る処理（<see cref="Produce"/>）は、一度に1つの裏のスレッドからしか呼ばない。
/// </summary>
public abstract class StreamingFrames : IAnimationFrames
{
    private readonly object _lock = new();
    private readonly Dictionary<int, BitmapSource> _buffer = [];
    private readonly int _ahead;
    private readonly int _behind;

    // 次に作るコマ。0 なら最初から作り直す（Reset してから作る）
    private int _next;

    // 今見せたいコマ（Prepare で受け取る）
    private int _wanted;

    // 裏で作っている最中か
    private bool _running;

    /// <param name="delays">各コマの表示時間（補正済み）</param>
    /// <param name="window">持っておくコマの枚数（3 以上にそろえる）</param>
    protected StreamingFrames(IReadOnlyList<TimeSpan> delays, int window)
    {
        if (delays.Count < 2)
        {
            throw new ArgumentException("アニメーションには2コマ以上必要です。", nameof(delays));
        }

        Delays = delays;
        int size = Math.Clamp(window, 3, delays.Count);
        _ahead = Math.Max(2, size * 3 / 4);
        _behind = Math.Max(0, size - _ahead);
    }

    public int Count => Delays.Count;

    public IReadOnlyList<TimeSpan> Delays { get; }

    public event EventHandler? FrameReady;

    /// <summary>今持っているコマの枚数（テスト用）</summary>
    public int BufferedCount
    {
        get { lock (_lock) return _buffer.Count; }
    }

    public BitmapSource? Get(int index)
    {
        lock (_lock)
        {
            // 見せたいコマが無ければ、手前に向かって、持っているいちばん近いコマを探す
            for (int k = 0; k < Count; k++)
            {
                if (_buffer.TryGetValue(Wrap(index - k), out var frame))
                {
                    return frame;
                }
            }
            return null;
        }
    }

    public void Prepare(int index)
    {
        lock (_lock)
        {
            _wanted = Wrap(index);
            if (_running || IsWindowReady())
            {
                return;
            }
            _running = true;
        }

        _ = Task.Run(Work);
    }

    /// <summary>最初のコマから作り直す準備（GIF なら合成用のキャンバスを空にする、WebP ならデコーダを巻き戻す）</summary>
    protected abstract void Reset();

    /// <summary>index のコマを作る。Reset の後、0, 1, 2, … の順でしか呼ばない。Freeze 済みで返すこと</summary>
    protected abstract BitmapSource Produce(int index);

    /// <summary>作り始める前に呼ぶ（同じスレッドで、この後 Produce が何回か呼ばれ、最後に <see cref="EndBatch"/>）</summary>
    protected virtual void BeginBatch()
    {
    }

    /// <summary>作り終わった後に呼ぶ（<see cref="BeginBatch"/> と同じスレッド）</summary>
    protected virtual void EndBatch()
    {
    }

    private void Work()
    {
        bool begun = false;
        try
        {
            while (true)
            {
                int target;
                lock (_lock)
                {
                    if (FirstMissing() is not { } missing)
                    {
                        _running = false;
                        return;
                    }

                    // 足りないコマが、もう作り終えた位置より手前（, で戻った・最初から再生し直した）なら、最初から作り直す。
                    // 先に進んで一周するより、最初から作るほうが早い
                    if (missing < _next)
                    {
                        _next = 0;
                    }
                    target = _next;
                }

                if (!begun)
                {
                    BeginBatch();
                    begun = true;
                }
                if (target == 0)
                {
                    Reset();
                }

                var frame = Produce(target); // 重い処理はロックの外で

                lock (_lock)
                {
                    _buffer[target] = frame;
                    _next = (target + 1) % Count;
                    Trim(keep: target);
                }
                FrameReady?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            // 作れなかった（壊れたコマ・メモリ不足など）。持っているコマで見せ続ける
            Debug.WriteLine($"アニメーションのコマを作れませんでした: {ex.Message}");
            lock (_lock)
            {
                _running = false;
            }
        }
        finally
        {
            if (begun)
            {
                EndBatch();
            }
        }
    }

    /// <summary>見せたいコマから先 _ahead 枚が、全部そろっているか（_lock の中で呼ぶ）</summary>
    private bool IsWindowReady() => FirstMissing() is null;

    /// <summary>見せたいコマから先 _ahead 枚のうち、最初に足りないコマ。全部そろっていれば null（_lock の中で呼ぶ）</summary>
    private int? FirstMissing()
    {
        for (int k = 0; k < _ahead; k++)
        {
            int index = Wrap(_wanted + k);
            if (!_buffer.ContainsKey(index))
            {
                return index;
            }
        }
        return null;
    }

    /// <summary>
    /// 見せたいコマの前後（手前 _behind 枚・先 _ahead 枚）以外を捨てる（_lock の中で呼ぶ）。
    /// keep（いま作ったコマ）は残す。遠くのコマまで作って追いつくあいだも、いちばん近いコマとして見せられるように
    /// </summary>
    private void Trim(int keep)
    {
        foreach (int index in _buffer.Keys.ToList())
        {
            int forward = Wrap(index - _wanted);  // 見せたいコマから先に何枚目か
            int backward = Wrap(_wanted - index); // 見せたいコマから手前に何枚目か
            if (index != keep && forward >= _ahead && backward > _behind)
            {
                _buffer.Remove(index);
            }
        }
    }

    private int Wrap(int index) => ((index % Count) + Count) % Count;
}
