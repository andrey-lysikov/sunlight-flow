//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Numerics;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;

using Color = Windows.UI.Color;

namespace SunlightFlow;

// Holds the LampArray devices and drives brightness towards the sun-derived goal.
// IsAvailable = false means Windows owns the lamps: writes succeed but go nowhere.
internal sealed class LightEngine : IAsyncDisposable
{
    // Ramp on a click: fast enough to read as a response to the gesture.
    private static readonly TimeSpan ToggleFade = TimeSpan.FromSeconds(2.5);

    // Daily drift: half a minute for the full span, so the change goes unnoticed.
    private static readonly TimeSpan DriftFade = TimeSpan.FromSeconds(30);

    // Frame rate while brightness is actually moving.
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(50);

    // Idle tick. Nothing is written, it only keeps the response to a click short.
    private static readonly TimeSpan IdleTick = TimeSpan.FromSeconds(1);

    // The sun barely moves in five minutes, and the fade smooths the step anyway.
    private static readonly TimeSpan LevelRefresh = TimeSpan.FromMinutes(5);

    // Standing still we still restate the colour now and then, in case something
    // else touched the device while we were idle.
    private static readonly TimeSpan KeepAlive = TimeSpan.FromMinutes(2);

    // Smoothing constant for the load tint, so the colour drifts instead of
    // stepping every time a new sample arrives.
    private static readonly TimeSpan LoadSmoothing = TimeSpan.FromSeconds(1.5);

    // Reading more often buys nothing and only keeps writing to the device.
    private static readonly TimeSpan LoadInterval = TimeSpan.FromSeconds(1);

    // How far a pulse dips at its lowest. A third reads as breathing.
    private const double PulseDepth = 1.0 / 3.0;

    private sealed class Target(LampArray array, string name)
    {
        public LampArray Array { get; } = array;
        public string Name { get; } = name;
        public Color[] Buffer { get; } = new Color[array.LampCount];
        public int[] Indices { get; } = [.. Enumerable.Range(0, (int)array.LampCount)];

        // Where each lamp sits, for the effects that move across the device.
        public Vector3[] Positions { get; } = Effects.Normalise(
            [.. Enumerable.Range(0, (int)array.LampCount).Select(i => array.GetLampInfo(i).Position)]);
    }

    private readonly List<Target> _targets = [];
    private readonly Lock _lock = new();
    private readonly LevelCalculator _calc = new();

    // Lets a toggle cut the idle wait short instead of losing up to a second.
    private readonly SemaphoreSlim _wake = new(0, 1);

    private readonly LoadMonitor _load = new();
    private volatile Task? _loadSample;
    private double _loadTarget;
    private double _loadLevel;

    private LightingState? _handover;
    private bool _handoverPending;
    private double _handoverBlend;

    private WindowsEffect _effect = WindowsEffect.Solid(ColorMath.Default);
    private Color? _loadColor;
    private double _effectPhase;
    private double _ceiling = 1.0;

    private bool _blood;
    private double _pulsePhase;
    private DateTime _bloodChecked = DateTime.MinValue;

    // Set when a lamp joins or comes back, so it gets a frame at once instead of
    // sitting dark until something moves or the keep-alive comes round.
    private volatile bool _devicesChanged;

    private volatile Task? _levelRefresh;
    private DateTime _levelStamp = DateTime.MinValue;

    private DeviceWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private AppConfig _config = AppConfig.Default;
    private Location _location = new(55.76, 37.62, "undetermined", ByIp: false);

    private volatile bool _enabled;
    private bool _toggling;
    private double _displayLevel;
    private double _targetLevel;

    private double _fadeFrom;
    private double _fadeTo;
    private DateTime _fadeStart;
    private TimeSpan _fadeDuration = ToggleFade;
    private bool _fadeReported = true;

    // Bumped by every toggle. A fade-out started earlier must not release the
    // lamps once the user has clicked the icon again.
    private long _generation;

    // Fires when the level, the device list or their availability changes.
    public event Action? StateChanged;

    public bool IsEnabled => _enabled;
    public double DisplayLevel => _displayLevel;
    public double SunElevation => _calc.LastElevation;
    public double SunFactor => _calc.LastSunFactor;
    public bool IsBloodMoon => _blood;
    public string EffectName => _effect.Type.ToString();
    public bool IsLoadSyncEnabled => _config.LoadEnabled;
    public double LoadLevel => _loadLevel;

    public int DeviceCount { get { lock (_lock) return _targets.Count; } }
    public int ControlledCount { get { lock (_lock) return _targets.Count(t => t.Array.IsAvailable); } }

    public void StartLoop()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    // Rereads the config and the location, which is why a click on the icon
    // doubles as the way to apply edits to the settings file.
    public async Task EnableAsync()
    {
        long generation = Interlocked.Increment(ref _generation);
        if (_enabled) return;

        _config = AppConfig.Load();
        RefreshEffect();

        // Read before the lamps are ours, while the registry still describes
        // what Windows is actually showing.
        _handover = WindowsLighting.Read();
        _handoverPending = _handover is not null;

        _location = await Geo.ResolveAsync(_config);

        // Resolving the location goes over the network, so another click may
        // have arrived in the meantime.
        if (Interlocked.Read(ref _generation) != generation) return;

        _targetLevel = await _calc.TargetAsync(_config, _location, _ceiling);

        // Everything was just read, so the loop need not do it all over again
        // in the middle of the handover.
        _levelStamp = DateTime.UtcNow;

        lock (_lock) { _toggling = true; _enabled = true; }
        Diagnostics.Log($"control on, goal {_targetLevel:F3}, sun {_calc.LastElevation:F1}°");

        StartWatcher();
        Wake();
        StateChanged?.Invoke();
    }

    // Fades down to zero first and only then hands the lamps back to Windows.
    public async Task DisableAsync()
    {
        long generation = Interlocked.Increment(ref _generation);
        if (!_enabled) return;

        lock (_lock) { _toggling = true; _enabled = false; }
        Diagnostics.Log("control going off, fading out");
        Wake();
        StateChanged?.Invoke();

        // Wait for the loop to reach zero, but not forever: the device could be
        // unplugged mid-fade.
        var deadline = DateTime.UtcNow + ToggleFade + TimeSpan.FromSeconds(2);
        while (_displayLevel > 0.001 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            if (Interlocked.Read(ref _generation) != generation)
            {
                Diagnostics.Log("fade-out abandoned, control was switched back on");
                return;
            }
        }

        if (Interlocked.Read(ref _generation) != generation) return;

        Release();
        Diagnostics.Log("control off, lamps returned to Windows");
        StateChanged?.Invoke();
    }

    public Task ToggleAsync() => _enabled ? DisableAsync() : EnableAsync();

    // Resume from sleep and session switches kill the previous grab, so the
    // device list is rebuilt from scratch.
    public void Rediscover(string reason)
    {
        if (!_enabled) return;

        Diagnostics.Log($"rebuilding the device list: {reason}");
        StopWatcher();
        lock (_lock) _targets.Clear();

        // Coming back to the console the lamps reappear with a lag, so the
        // first sweep after the switch would otherwise find nothing.
        _toggling = true;
        StartWatcher();
        StateChanged?.Invoke();
    }

    private void StartWatcher()
    {
        if (_watcher is not null) return;

        _watcher = DeviceInformation.CreateWatcher(LampArray.GetDeviceSelector());
        _watcher.Added += OnAdded;
        _watcher.Removed += OnRemoved;
        _watcher.Start();
    }

    private void StopWatcher()
    {
        if (_watcher is null) return;

        _watcher.Added -= OnAdded;
        _watcher.Removed -= OnRemoved;
        try { _watcher.Stop(); } catch { /* already stopped */ }
        _watcher = null;
    }

    // Releases the lamps: without this Windows never restores its own effect.
    private void Release()
    {
        StopWatcher();

        Target[] snapshot;
        lock (_lock)
        {
            snapshot = [.. _targets];
            _targets.Clear();
        }

        foreach (var t in snapshot)
        {
            if (!t.Array.IsAvailable) continue;
            try { t.Array.SetColor(Color.FromArgb(255, 0, 0, 0)); } catch { /* already taken away */ }
        }

        _displayLevel = 0;
    }

    private async void OnAdded(DeviceWatcher _, DeviceInformation info)
    {
        try
        {
            var array = await LampArray.FromIdAsync(info.Id);
            if (array is null || array.LampCount == 0) return;

            array.AvailabilityChanged += (a, _) =>
            {
                Diagnostics.Log($"{info.Name}: IsAvailable -> {a.IsAvailable}");

                // Windows stops drawing the moment the lamps are ours.
                if (a.IsAvailable)
                {
                    _devicesChanged = true;
                    Wake();
                }

                StateChanged?.Invoke();
            };

            lock (_lock)
            {
                if (_targets.Any(t => t.Array.DeviceId == array.DeviceId)) return;
                _targets.Add(new Target(array, info.Name));
            }

            _devicesChanged = true;
            Wake();

            Diagnostics.Log(
                $"found «{info.Name}»: kind={array.LampArrayKind}, lamps={array.LampCount}, " +
                $"IsAvailable={array.IsAvailable}, MinUpdate={array.MinUpdateInterval.TotalMilliseconds:F0} ms");

            StateChanged?.Invoke();
        }
        catch (Exception e)
        {
            Diagnostics.Log($"could not open «{info.Name}»: {e.Message}");
        }
    }

    private void OnRemoved(DeviceWatcher _, DeviceInformationUpdate upd)
    {
        bool removed;
        lock (_lock) removed = _targets.RemoveAll(t => t.Array.DeviceId == upd.Id) > 0;
        if (removed) StateChanged?.Invoke();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var logStamp = DateTime.MinValue;
        var lostSince = DateTime.MinValue;
        int written = 0;

        var lastWrite = DateTime.MinValue;
        var loadStamp = DateTime.MinValue;
        var lastFrame = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - lastFrame;
            lastFrame = now;

            if (_enabled && now - _levelStamp > LevelRefresh && (_levelRefresh?.IsCompleted ?? true))
            {
                _levelStamp = now;
                _levelRefresh = Task.Run(() => RefreshLevelAsync(ct), ct);
            }

            // Cleared before the snapshot, so a lamp arriving in between is either
            // in it or raises the flag again for the next pass.
            bool devicesChanged = _devicesChanged;
            _devicesChanged = false;

            // One view of the lamps for the whole pass: one that turned up between
            // the handover check and the write would otherwise get a dark frame first.
            Target[] live;
            lock (_lock) live = [.. _targets.Where(t => t.Array.IsAvailable)];

            // The ramp may only start once a lamp is actually ours, otherwise it
            // would run out while Windows still owns the device.
            if (_handoverPending && live.Length > 0 && _handover is not null)
            {
                _handoverPending = false;
                // Windows scales the colour by its slider linearly, while our level is
                // gamma-encoded on the way out. Undone here, so the first frame puts
                // out exactly what Windows was showing instead of dipping to its square.
                _displayLevel = Math.Pow(_handover.Brightness, 1.0 / _config.Gamma);
                _handoverBlend = 1.0;

                // Out of range on purpose, so the next Advance sees a new goal
                // and starts the ramp from where Windows left the lamps.
                _fadeTo = -1.0;
                _toggling = true;
                Diagnostics.Log($"taking over from {ColorMath.ToHex(_handover.Color)} " +
                                $"at {_handover.Brightness * 100:F0}%");
            }

            double goal = _enabled ? _targetLevel : 0.0;
            bool moving = Advance(goal);

            if (now - _bloodChecked > TimeSpan.FromMinutes(5))
            {
                _bloodChecked = now;
                bool blood = _enabled && _calc.LastElevation < 0 && Moon.IsBlood(now);
                if (blood != _blood)
                {
                    _blood = blood;
                    Diagnostics.Log(blood ? "blood moon tonight" : "blood moon over");
                    StateChanged?.Invoke();
                }
            }

            // Dark lamps have nothing to tint, so the load is not measured. A blood
            // moon needs it anyway: it sets the pulse rate.
            bool tinting = (_config.LoadEnabled || _blood) && _displayLevel > 0.0005;

            // Off the loop thread: enumerating the graphics counters takes long
            // enough on the first pass to stall the ramp visibly.
            if (tinting && now - loadStamp > LoadInterval && (_loadSample?.IsCompleted ?? true))
            {
                loadStamp = now;
                _loadSample = Task.Run(() => Volatile.Write(ref _loadTarget, _load.Sample()), ct);
            }

            bool tintMoving = AdvanceLoad(tinting ? Volatile.Read(ref _loadTarget) : 0.0, elapsed);
            moving |= tintMoving;

            var effect = _effect;
            bool lit = _displayLevel > 0.0005;

            // Gradient and breathing show the load as pace rather than tint.
            bool tintByLoad = _config.LoadEnabled && effect.TintsUnderLoad;
            bool paceByLoad = _config.LoadEnabled && !effect.TintsUnderLoad;

            // A moving Windows effect keeps the frames coming while the lamps are lit.
            if (effect.IsAnimated && lit)
            {
                // Breathing already pulses, so load only speeds it up, up to four times.
                double pace = paceByLoad ? 1 + 3 * _loadLevel : 1;
                double dt = Math.Clamp(elapsed.TotalSeconds, 0, 1.0);
                _effectPhase = (_effectPhase + Effects.Rate(effect) * pace * dt) % 1.0;
                moving = true;
            }

            // A still effect gets a pulse of its own under load; a blood moon always pulses.
            bool pulsing = lit && (_blood || (paceByLoad && !effect.IsAnimated && _loadLevel > 0.02));
            moving |= pulsing;

            // Standing at the goal there is nothing to send, so the device is
            // left alone apart from an occasional restatement of the colour.
            bool due = moving || devicesChanged || DateTime.UtcNow - lastWrite > KeepAlive;
            var frameDelay = moving ? Frame : IdleTick;

            if (due)
            {
                // The level is perceptual; the LED needs it gamma-encoded.
                double output = Math.Pow(_displayLevel, _config.Gamma);

                if (pulsing)
                {
                    // Faster the busier the machine, never above three beats a second.
                    double rate = Math.Clamp(0.5 + 2.5 * _loadLevel, 0.5, 3.0);
                    _pulsePhase = (_pulsePhase + rate * Frame.TotalSeconds) % 1.0;

                    // Breathing, not blinking: a third off at most, so the light never drops out.
                    double depth = _blood ? PulseDepth : PulseDepth * Math.Clamp(_loadLevel, 0, 1);
                    double wave = 0.5 - 0.5 * Math.Cos(2 * Math.PI * _pulsePhase);

                    output *= 1.0 - depth * (1.0 - wave);
                }

                // Colour crosses over on the same schedule as brightness, so the
                // handover reads as one movement rather than two.
                var handover = _handover;
                double handoverBlend = handover is null ? 0 : _handoverBlend;
                if (handoverBlend > 0)
                {
                    _handoverBlend = Math.Max(0, _handoverBlend - Frame.TotalSeconds / ToggleFade.TotalSeconds);
                    moving = true;
                }

                var loadColor = _loadColor;

                Color Shade(Color c)
                {
                    if (tintByLoad && loadColor is { } load) c = ColorMath.Mix(c, load, _loadLevel);

                    // A total lunar eclipse takes the palette over entirely.
                    if (_blood) c = Color.FromArgb(255, 255, 0, 0);

                    if (handoverBlend > 0) c = ColorMath.Mix(c, handover!.Color, handoverBlend);
                    return ColorMath.Scale(c, output);
                }

                lastWrite = DateTime.UtcNow;

                foreach (var t in live)
                {
                    if (!t.Array.IsAvailable) continue;
                    try
                    {
                        Effects.Render(effect, _effectPhase, t.Positions, t.Buffer);
                        for (int i = 0; i < t.Buffer.Length; i++)
                            t.Buffer[i] = Shade(t.Buffer[i]);

                        t.Array.SetColorsForIndices(t.Buffer, t.Indices);
                        written++;

                        // Never push faster than the device allows.
                        if (moving && t.Array.MinUpdateInterval > frameDelay)
                            frameDelay = t.Array.MinUpdateInterval;
                    }
                    catch (Exception e)
                    {
                        Diagnostics.Log($"write to «{t.Name}» failed: {e.Message}");
                    }
                }
            }

            // Control can end up on with the watcher stopped, for instance when
            // a fade-out finished after the user switched it back on.
            if (_enabled && _watcher is null) StartWatcher();

            // A safety net for losses no event reports: a disconnected session,
            // a device reset, another app taking the lamps and giving them back.
            if (_enabled && ControlledCount == 0)
            {
                if (lostSince == DateTime.MinValue) lostSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - lostSince > TimeSpan.FromMinutes(2))
                {
                    lostSince = DateTime.MinValue;
                    Rediscover("no controlled device for two minutes");
                }
            }
            else
            {
                lostSince = DateTime.MinValue;
            }

            if (DateTime.UtcNow - logStamp > TimeSpan.FromMinutes(5))
            {
                logStamp = DateTime.UtcNow;
                string load = _config.LoadEnabled ? $", load {_loadLevel * 100:F0}%" : "";
                Diagnostics.Log(
                    $"brightness {_displayLevel:F3} -> {goal:F3}, sun {_calc.LastElevation:F1}°{load}, " +
                    $"frames {written}, devices {DeviceCount}, controlled {ControlledCount}");
                written = 0;
            }

            try { await _wake.WaitAsync(frameDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Exponential drift towards the latest sample: the tint keeps flowing between
    // readings instead of stepping once a second.
    private bool AdvanceLoad(double target, TimeSpan elapsed)
    {
        double delta = target - _loadLevel;
        if (Math.Abs(delta) < 0.002)
        {
            _loadLevel = target;
            return false;
        }

        double dt = Math.Clamp(elapsed.TotalSeconds, 0, 1.0);
        _loadLevel += delta * (1.0 - Math.Exp(-dt / LoadSmoothing.TotalSeconds));
        return true;
    }

    // Off the loop thread: the IP lookup and the weather can take seconds, and
    // the lamps must not freeze mid-ramp meanwhile.
    private async Task RefreshLevelAsync(CancellationToken ct)
    {
        try
        {
            // Picks up a different effect or brightness chosen in Settings meanwhile.
            RefreshEffect();

            // At boot the network is often not up yet, so the IP lookup is retried.
            if (!_location.ByIp) _location = await Geo.ResolveAsync(_config, ct);

            _targetLevel = await _calc.TargetAsync(_config, _location, _ceiling);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Diagnostics.Log($"level refresh failed: {e.Message}");
        }
    }

    private void RefreshEffect()
    {
        var effect = Effects.Resolve();
        var loadColor = Effects.LoadColor(effect);

        // With Dynamic Lighting off in Windows there is no slider to follow.
        double ceiling = WindowsLighting.ReadBrightness() ?? 1.0;

        if (!effect.Equals(_effect) || !loadColor.Equals(_loadColor) || ceiling != _ceiling)
            Diagnostics.Log($"effect {effect}, load colour {(loadColor is { } load ? ColorMath.ToHex(load) : "none")}, " +
                            $"ceiling {ceiling * 100:F0}%");

        _effect = effect;
        _loadColor = loadColor;
        _ceiling = ceiling;
    }

    private void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { /* already pending */ }
    }

    // Fixed-span ramp to the goal, so a toggle takes the same time whatever the
    // ceiling. Returns true while still moving.
    private bool Advance(double goal)
    {
        if (Math.Abs(goal - _fadeTo) > 1e-9)
        {
            _fadeFrom = _displayLevel;
            _fadeTo = goal;
            _fadeStart = DateTime.UtcNow;
            _fadeDuration = _toggling ? ToggleFade : DriftFade;
            _fadeReported = Math.Abs(_fadeTo - _displayLevel) < 0.0005;
            _toggling = false;
        }

        if (_fadeReported && Math.Abs(_displayLevel - _fadeTo) < 0.0005)
        {
            _displayLevel = _fadeTo;
            return false;
        }

        double span = _fadeDuration.TotalSeconds;
        double t = span <= 0 ? 1.0 : (DateTime.UtcNow - _fadeStart).TotalSeconds / span;
        t = Math.Clamp(t, 0.0, 1.0);

        if (t >= 1.0 && !_fadeReported)
        {
            _fadeReported = true;
            Diagnostics.Log($"reached {_fadeTo:F3} in {(DateTime.UtcNow - _fadeStart).TotalSeconds:F1} s");
        }

        // Eased in: the change starts barely noticeable and gathers pace towards
        // the end, both when the light comes up and when it goes down.
        double eased = t * t * t;

        _displayLevel = Math.Clamp(_fadeFrom + (_fadeTo - _fadeFrom) * eased, 0.0, 1.0);
        StateChanged?.Invoke();
        return t < 1.0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_enabled)
        {
            try { await DisableAsync(); }
            catch (Exception e) { Debug.WriteLine(e.Message); }
        }

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_loop is not null)
            {
                try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* did not finish in time */ }
            }
            _cts.Dispose();
        }

        Release();
        _load.Dispose();
    }
}
