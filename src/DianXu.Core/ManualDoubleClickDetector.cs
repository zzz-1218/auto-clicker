namespace DianXu.Core;

public sealed class ManualDoubleClickDetector(uint intervalMilliseconds)
{
    private bool _hasFirst;
    private uint _lastTime;
    public void Reset() => _hasFirst = false;

    public bool ObserveLeftDown(uint time, bool injected)
    {
        if (injected) return false;
        // Only physical events count. Ignore coordinates because the clicker moves the pointer.
        if (_hasFirst && unchecked(time - _lastTime) <= intervalMilliseconds)
        {
            _hasFirst = false;
            return true;
        }
        _hasFirst = true;
        _lastTime = time;
        return false;
    }
}
