public sealed class VaultStatusProvider(VaultStatus initial)
{
    private readonly Lock _lock = new();
    private VaultStatus _current = initial;
    private bool _announcing;

    public VaultStatus Current
    {
        get { lock (_lock) return _current; }
        set
        {
            lock (_lock)
            {
                if (_current == value) return;
                _current = value;
                if (_announcing) Write();
            }
        }
    }

    /// <summary>
    /// Writes the current status to stdout for the bridge, then every change after it. Call it once the port is
    /// bound: the bridge takes the first VAULT_STATUS line to mean this process is listening.
    /// </summary>
    public void StartAnnouncing()
    {
        lock (_lock)
        {
            _announcing = true;
            Write();
        }
    }

    // Under the lock, so the lines come out in the order the changes happened.
    private void Write() => Console.WriteLine("VAULT_STATUS:" + _current);
}