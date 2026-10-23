// Flipping the donut to its other face: every profile has a front and a back list of slices, and Tab turns the open
// donut over. The ring turns about the vertical axis like a coin (its width goes 1 → 0, the faces swap while it is
// edge-on, 0 → 1); the hub stays where it is and says how to flip ("Tab ↻ back").
//
// The flip key (Tab unless the user records another in Settings → Shortcuts) and the mouse wheel flip it:
//   Click mode → the key reaches the focused window (HandleKey), the wheel the donut (OnPointerWheelChanged).
//   Drag mode  → the window has no keyboard focus during an OS drag: RadialController flips when the global hook
//                sees the key or the wheel (TriggerService.FlipKeyPressed / FlipWheelTurned).
//
// The control only animates: the owner swaps Items when FaceSwapping is raised (RadialWindow → RadialViewModel), so
// the slices, hit-testing and what a drop converts with always belong to the face that is drawn.

using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Filee.App.Services;

namespace Filee.App.Controls;

public sealed partial class DonutMenu
{
    /// <summary>There is another face with slices for these files: the flip key or the wheel turns to it.</summary>
    public static readonly StyledProperty<bool> CanFlipProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(CanFlip));

    /// <summary>Small line at the bottom of the hub, e.g. "Tab ↻ back" (null for none).</summary>
    public static readonly StyledProperty<string?> CenterHintProperty =
        AvaloniaProperty.Register<DonutMenu, string?>(nameof(CenterHint));

    /// <summary>A small mouse in front of <see cref="CenterHint"/>: the wheel flips (and no key does).</summary>
    public static readonly StyledProperty<bool> CenterHintWheelProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(CenterHintWheel));

    /// <summary>The key that flips the donut in click mode, as the settings name it ("Tab", "F2"); null for none.</summary>
    public static readonly StyledProperty<string?> FlipKeyProperty =
        AvaloniaProperty.Register<DonutMenu, string?>(nameof(FlipKey));

    /// <summary>A turn of the mouse wheel over the donut flips it (click mode; drag mode reads the wheel globally).</summary>
    public static readonly StyledProperty<bool> FlipWithWheelProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(FlipWithWheel));

    /// <summary>
    /// A wheel event this soon after the previous one belongs to the same turn of the wheel: one flip per turn, however
    /// many notches (or a touchpad's many small steps) it has, so a fast scroll doesn't flip back and forth.
    /// </summary>
    internal const long WheelQuietMs = 300;

    private long? _lastWheel;

    /// <summary>How long a flip takes, there and back: the faces swap halfway.</summary>
    internal const double FlipDuration = 0.2;

    private double _flipClock = -1;   // seconds into the running flip; -1 none
    private bool _flipSwapped;        // the running flip swapped the faces already
    private bool _flipQueued;         // Tab again during a flip: flip once more after it
    private bool _swapping;           // FaceSwapping is being handled (Items change for the flip, not for new files)
    private Point? _lastExternalAt;   // where a drag from outside was last seen on the donut

    public bool CanFlip { get => GetValue(CanFlipProperty); set => SetValue(CanFlipProperty, value); }
    public string? CenterHint { get => GetValue(CenterHintProperty); set => SetValue(CenterHintProperty, value); }
    public bool CenterHintWheel { get => GetValue(CenterHintWheelProperty); set => SetValue(CenterHintWheelProperty, value); }
    public string? FlipKey { get => GetValue(FlipKeyProperty); set => SetValue(FlipKeyProperty, value); }
    public bool FlipWithWheel { get => GetValue(FlipWithWheelProperty); set => SetValue(FlipWithWheelProperty, value); }

    /// <summary>
    /// A wheel event at <paramref name="nowMs"/> (milliseconds, any clock): flips on the first one of a turn of the wheel
    /// and ignores the rest of it (<see cref="WheelQuietMs"/>). Returns true when it flipped.
    /// </summary>
    public bool FlipFromWheel(long nowMs)
    {
        var sameTurn = _lastWheel is { } last && nowMs - last < WheelQuietMs;
        _lastWheel = nowMs;
        return !sameTurn && Flip();
    }

    /// <summary>Click mode: the wheel over the donut flips it; nothing under the donut scrolls meanwhile.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!FlipWithWheel || IsEditMode || _progressMode || e.Delta.Y == 0)
            return;
        FlipFromWheel(Environment.TickCount64);
        e.Handled = true;
    }

    /// <summary>
    /// The donut is edge-on (at once without animations): the owner shows the other face now by changing
    /// <see cref="Items"/>, <see cref="CanFlip"/> and <see cref="CenterHint"/>.
    /// </summary>
    public event EventHandler? FaceSwapping;

    /// <summary>A flip is running.</summary>
    internal bool IsFlipping => _flipClock >= 0;

    /// <summary>How wide the ring is drawn now: 1 normally, down to 0 while a flip has it edge-on.</summary>
    internal double FlipScale => _flipClock < 0 ? 1 : Math.Abs(Math.Cos(Math.PI * Math.Min(_flipClock / FlipDuration, 1)));

    /// <summary>
    /// Turns the donut over to its other face (Tab), folding an open group's ring away first. Without animations the
    /// faces swap at once. Returns false when there is nothing to flip to (<see cref="CanFlip"/>), in the editor or
    /// while the donut shows a conversion's progress.
    /// </summary>
    public bool Flip()
    {
        if (IsEditMode || _progressMode || _closing)
            return false;
        if (IsFlipping)
        {
            // Pressed again before the turn ended: once more afterwards (twice cancels that out again).
            _flipQueued = !_flipQueued;
            return true;
        }
        if (!CanFlip)
            return false;
        CollapseGroup();
        if (!Motion.Enabled)
        {
            SwapFaces();
            return true;
        }
        _flipClock = 0;
        _flipSwapped = false;
        StartAnimation();
        return true;
    }

    /// <summary>
    /// The second half of a flip for items that were already swapped (the editor's Front / Back switch): the ring
    /// turns in from edge-on. Nothing without animations.
    /// </summary>
    public void PlayFlipIn()
    {
        if (!Motion.Enabled)
            return;
        _flipClock = FlipDuration / 2;
        _flipSwapped = true;
        _flipQueued = false;
        StartAnimation();
    }

    /// <summary>Advances a running flip. Returns true while it needs more frames.</summary>
    private bool StepFlip(double dt)
    {
        if (_flipClock < 0)
            return false;
        _flipClock += dt;
        if (!_flipSwapped && _flipClock >= FlipDuration / 2)
            SwapFaces();
        if (_flipClock < FlipDuration)
            return true;
        EndFlip();
        return IsFlipping; // a queued flip started
    }

    /// <summary>Ends a running flip where it ends (the faces swapped): animations off or frames stalled.</summary>
    private void FinishFlip()
    {
        while (IsFlipping)
        {
            if (!_flipSwapped)
                SwapFaces();
            EndFlip();
        }
    }

    private void EndFlip()
    {
        _flipClock = -1;
        if (!_flipQueued)
            return;
        _flipQueued = false;
        Flip();
    }

    private void CancelFlip()
    {
        _flipClock = -1;
        _flipQueued = false;
    }

    /// <summary>
    /// Has the owner show the other face, then lights up what is under the drag or the mouse on it, as if it had just
    /// moved there: a keyboard selection starts over (the arrows begin at the first slice again).
    /// </summary>
    private void SwapFaces()
    {
        _flipSwapped = true;
        var dragAt = _lastExternalAt;
        var pointer = _keyboardHold ? null : _lastPointer;
        _swapping = true;
        try
        {
            FaceSwapping?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _swapping = false;
        }

        if (dragAt is { } at)
        {
            SetExternalHit(HitTest(at), at);
        }
        else if (pointer is { } p)
        {
            _lastPointer = p;
            var hit = HitTest(p);
            TrackGroups(hit, p);
            SetHover(hit.Kind is DonutHitKind.Segment or DonutHitKind.SubSegment ? hit.Index : -1);
        }
        ActiveIndexChanged?.Invoke(this, ActiveIndex); // the hub's text follows the new face
        InvalidateVisual();
    }

    /// <summary>Squeezes the ring horizontally about <paramref name="center"/> by <see cref="FlipScale"/>.</summary>
    private Matrix FlipTransform(Point center)
    {
        var scale = FlipScale;
        if (scale >= 1)
            return Matrix.Identity;
        // Never quite 0: a singular transform can't be inverted for hit tests and clips.
        return Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateScale(Math.Max(scale, 0.01), 1) *
               Matrix.CreateTranslation(center.X, center.Y);
    }
}
