using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageViewer.Imaging;

namespace ImageViewer.Animation;

/// <summary>
/// アニメーションの再生。DispatcherTimer で次のコマの時刻に起きて、<see cref="AnimationTimeline"/> を進める。
/// 実際の経過時間（Stopwatch）で進めるので、タイマーが遅れてもコマを飛ばして追いつく。
/// UIスレッド専用。
/// </summary>
public sealed class AnimationPlayer
{
    // タイマーの最短間隔（短すぎると UI が詰まる）
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(10);

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastTick;

    private DecodedImage? _image;
    private AnimationTimeline? _timeline;
    private bool _suspended;

    public AnimationPlayer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += OnTick;
    }

    /// <summary>表示するコマや、再生・一時停止の状態が変わったとき</summary>
    public event EventHandler? Changed;

    /// <summary>最後のコマまで再生し終わったとき（1周ごと。スライドショーで次の画像へ進むのに使う）</summary>
    public event EventHandler? PlayCompleted;

    /// <summary>アニメーションを読み込んでいるか（静止画なら false）</summary>
    public bool IsAnimated => _timeline is not null;

    /// <summary>ユーザーが一時停止しているか</summary>
    public bool IsPaused { get; private set; }

    /// <summary>決められた回数を再生し終わって止まっているか</summary>
    public bool IsFinished => _timeline?.IsFinished ?? false;

    public int FrameIndex => _timeline?.Index ?? 0;

    public int FrameCount => _timeline?.FrameCount ?? 0;

    /// <summary>今表示するコマ。アニメーションでなければ null</summary>
    public BitmapSource? CurrentFrame =>
        _image?.Frames is { } frames && _timeline is not null ? frames[_timeline.Index].Image : null;

    /// <summary>
    /// 最小化などで一時的に止める（ユーザーの一時停止とは別）。解除すると、止めたところから続ける。
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended != value)
            {
                _suspended = value;
                UpdateTimer();
            }
        }
    }

    /// <summary>新しい画像に切り替える。アニメーションなら最初から自動再生する。null や静止画なら止める。</summary>
    public void Load(DecodedImage? image)
    {
        _timer.Stop();
        _image = image;
        _timeline = image?.Frames is { } frames
            ? new AnimationTimeline(frames.Select(f => f.Delay).ToList(), image!.LoopCount)
            : null;
        IsPaused = false;
        UpdateTimer();
    }

    /// <summary>一時停止 ⇔ 再生。再生し終わって止まっているときは、最初から再生し直す。</summary>
    public void TogglePause()
    {
        if (_timeline is null) return;

        if (_timeline.IsFinished)
        {
            _timeline.Restart();
            IsPaused = false;
        }
        else
        {
            IsPaused = !IsPaused;
        }

        UpdateTimer();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>1コマ進める / 戻す。コマ送りしたら一時停止にする。</summary>
    public void Step(int direction)
    {
        if (_timeline is null) return;

        _timeline.Step(direction);
        IsPaused = true;
        UpdateTimer();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateTimer()
    {
        bool shouldRun = _timeline is not null && !IsPaused && !_suspended && !_timeline.IsFinished;

        if (!shouldRun)
        {
            _timer.Stop();
            return;
        }

        if (!_timer.IsEnabled)
        {
            // 止まっていた間の時間は進めない
            _lastTick = _clock.Elapsed;
            Schedule();
        }
    }

    private void Schedule()
    {
        var remaining = _timeline!.RemainingInFrame;
        _timer.Interval = remaining > MinInterval ? remaining : MinInterval;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_timeline is null) return;

        var now = _clock.Elapsed;
        var elapsed = now - _lastTick;
        _lastTick = now;

        int playsBefore = _timeline.CompletedPlays;
        bool changed = _timeline.Advance(elapsed);
        bool completed = _timeline.CompletedPlays > playsBefore;

        if (!_timeline.IsFinished && !IsPaused && !_suspended)
        {
            Schedule();
        }

        if (changed || _timeline.IsFinished)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // Changed の中で別の画像に切り替わっていることもあるので、最後に知らせる
        if (completed)
        {
            PlayCompleted?.Invoke(this, EventArgs.Empty);
        }
    }
}
