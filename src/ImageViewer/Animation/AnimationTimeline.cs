namespace ImageViewer.Animation;

/// <summary>
/// アニメーションの「今どのコマか」を経過時間から決める。タイマーや表示には関与しない（テストしやすいように）。
/// <para>
/// 経過時間をまとめて渡せば、その間に過ぎたコマを飛ばして追いつく（重いときにスローモーションにならない）。
/// </para>
/// </summary>
public sealed class AnimationTimeline
{
    private readonly TimeSpan[] _delays;
    private readonly TimeSpan _totalDuration;
    private readonly int _loopCount;

    // 最後まで再生し終わった回数
    private int _completedPlays;

    /// <param name="delays">各コマの表示時間（0以下のものは 1ms として扱う）</param>
    /// <param name="loopCount">再生する回数。0 なら無限ループ</param>
    public AnimationTimeline(IReadOnlyList<TimeSpan> delays, int loopCount)
    {
        if (delays.Count == 0)
        {
            throw new ArgumentException("コマが1枚もありません。", nameof(delays));
        }

        _delays = delays.Select(d => d > TimeSpan.Zero ? d : TimeSpan.FromMilliseconds(1)).ToArray();
        _totalDuration = TimeSpan.FromTicks(_delays.Sum(d => d.Ticks));
        _loopCount = Math.Max(0, loopCount);
        RemainingInFrame = _delays[0];
    }

    public int FrameCount => _delays.Length;

    /// <summary>今表示するコマ（0始まり）</summary>
    public int Index { get; private set; }

    /// <summary>決められた回数を再生し終わって、最後のコマで止まっているか</summary>
    public bool IsFinished { get; private set; }

    /// <summary>今のコマを、あとどれだけ表示するか</summary>
    public TimeSpan RemainingInFrame { get; private set; }

    /// <summary>時間を進める。コマが変わったら true。</summary>
    public bool Advance(TimeSpan elapsed)
    {
        if (IsFinished || elapsed <= TimeSpan.Zero)
        {
            return false;
        }

        // 無限ループで長時間止まっていた（スリープ復帰など）ときに、何周もぐるぐる回さない
        if (_loopCount == 0 && elapsed > _totalDuration)
        {
            elapsed = TimeSpan.FromTicks(elapsed.Ticks % _totalDuration.Ticks);
        }

        bool changed = false;
        while (elapsed >= RemainingInFrame)
        {
            elapsed -= RemainingInFrame;

            if (Index == _delays.Length - 1)
            {
                _completedPlays++;
                if (_loopCount > 0 && _completedPlays >= _loopCount)
                {
                    // 最後のコマで止める
                    IsFinished = true;
                    RemainingInFrame = TimeSpan.Zero;
                    return changed;
                }
                Index = 0;
            }
            else
            {
                Index++;
            }

            RemainingInFrame = _delays[Index];
            changed = true;
        }

        RemainingInFrame -= elapsed;
        return changed;
    }

    /// <summary>手動で1コマ進める / 戻す（端ではぐるっと回る）。止まっていた状態は解除される。</summary>
    public void Step(int direction)
    {
        int count = _delays.Length;
        Index = ((Index + Math.Sign(direction)) % count + count) % count;
        RemainingInFrame = _delays[Index];
        IsFinished = false;
    }

    /// <summary>最初から再生し直す</summary>
    public void Restart()
    {
        Index = 0;
        _completedPlays = 0;
        IsFinished = false;
        RemainingInFrame = _delays[0];
    }
}
