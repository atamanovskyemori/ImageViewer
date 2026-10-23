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
/// <item>持っていない手前のコマに戻るときは、作り直す。途中の状態を取っておけるもの（GIF）は、いちばん近い途中から。それ以外は最初から</item>
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

    // 途中の状態（キー：そのコマを作り終えた時点）。手前に戻るとき、最初からではなくここから作り直す
    private readonly Dictionary<int, object> _checkpoints = [];

    // 途中の状態を取っておく間隔（何コマごとか）。0 なら取っておかない
    private readonly int _checkpointInterval;

    /// <param name="delays">各コマの表示時間（補正済み）</param>
    /// <param name="window">持っておくコマの枚数（3 以上にそろえる）</param>
    /// <param name="maxCheckpoints">
    /// 途中の状態を取っておく数（<see cref="SaveState"/> が使えるものだけ。0 なら取っておかない）。
    /// 全体に均等にばらまくので、手前に戻るときに作り直す枚数は、多くても「コマ数 ÷ (この数 + 1)」くらいになる
    /// </param>
    protected StreamingFrames(IReadOnlyList<TimeSpan> delays, int window, int maxCheckpoints = 0)
    {
        if (delays.Count < 2)
        {
            throw new ArgumentException("アニメーションには2コマ以上必要です。", nameof(delays));
        }

        Delays = delays;
        int size = Math.Clamp(window, 3, delays.Count);
        _ahead = Math.Max(2, size * 3 / 4);
        _behind = Math.Max(0, size - _ahead);
        _checkpointInterval = maxCheckpoints > 0 ? (delays.Count + maxCheckpoints) / (maxCheckpoints + 1) : 0;
    }

    /// <summary>取っておいた途中の状態の数（テスト用）</summary>
    public int CheckpointCount
    {
        get { lock (_lock) return _checkpoints.Count; }
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

    /// <summary>
    /// 今の途中の状態（直前に作ったコマまで進んだ状態）を写して返す。取っておけないものは null（WebP など）。
    /// 返したものは <see cref="RestoreState"/> に何度でも渡せるように、作り直すときに書き換えないこと
    /// </summary>
    protected virtual object? SaveState() => null;

    /// <summary><see cref="SaveState"/> で取っておいた状態に戻す（この後、その次のコマから Produce が呼ばれる）</summary>
    protected virtual void RestoreState(object state)
    {
    }

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
                object? restore = null;
                lock (_lock)
                {
                    if (FirstMissing() is not { } missing)
                    {
                        _running = false;
                        return;
                    }

                    // 足りないコマまで、どこから作り始めるのがいちばん早いか（今の続き・最初から・取っておいた途中から）
                    (target, restore) = ChooseStart(missing);
                    _next = target;
                }

                if (!begun)
                {
                    BeginBatch();
                    begun = true;
                }
                if (restore is not null)
                {
                    RestoreState(restore);
                }
                else if (target == 0)
                {
                    Reset();
                }

                var frame = Produce(target); // 重い処理はロックの外で

                // ところどころで、作り直すときの出発点を取っておく（ロックの外で写す）
                object? checkpoint = ShouldSaveCheckpoint(target) ? SaveState() : null;

                lock (_lock)
                {
                    _buffer[target] = frame;
                    _next = (target + 1) % Count;
                    Trim(keep: target);
                    if (checkpoint is not null)
                    {
                        _checkpoints[target] = checkpoint;
                    }
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

    /// <summary>
    /// 足りないコマ（missing）まで作るのに、どこから作り始めるか（_lock の中で呼ぶ）。
    /// 作る枚数がいちばん少ないものを選ぶ：
    /// <list type="bullet">
    /// <item>今の続き（missing がまだ先にあるとき）</item>
    /// <item>最初から（Reset）</item>
    /// <item>取っておいた途中の状態から（missing より手前で、いちばん近いもの）</item>
    /// </list>
    /// </summary>
    /// <returns>最初に作るコマと、戻す状態（今の続き・最初からなら null）</returns>
    private (int Start, object? State) ChooseStart(int missing)
    {
        int start = _next;
        object? state = null;
        int cost = missing >= _next ? missing - _next : int.MaxValue;

        if (missing < cost)
        {
            start = 0;
            cost = missing;
        }

        foreach (var (index, saved) in _checkpoints)
        {
            // index のコマを作り終えた状態なので、次は index + 1 から
            if (index < missing && missing - (index + 1) < cost)
            {
                start = index + 1;
                state = saved;
                cost = missing - (index + 1);
            }
        }
        return (start, state);
    }

    /// <summary>index のコマを作り終えたところで、途中の状態を取っておくか（最初の一周で、決まった間隔ごと）</summary>
    private bool ShouldSaveCheckpoint(int index)
    {
        if (_checkpointInterval <= 0 || (index + 1) % _checkpointInterval != 0 || index >= Count - 1)
        {
            return false;
        }
        lock (_lock)
        {
            return !_checkpoints.ContainsKey(index);
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
