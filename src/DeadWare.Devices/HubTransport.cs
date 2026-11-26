namespace DeadWare.Devices;

// Lighting 5: the hub's HID link and the lock other Corsair software shares, behind interfaces so the worker is tested against fakes.
internal interface IHubTransport
{
    // The hubs Windows lists: 1B1C:0C3F with usage page FF42, usage 1.
    int Count();
    // That hub, shared; throws when it cannot.
    IHubPort Open();
}

// Dispose may be called from another thread and ends a call in progress.
internal interface IHubPort:IDisposable
{
    // The only way to send anything: the frame's 513 bytes. Throws TimeoutException past 250 ms, IOException on a device error.
    void Write(HubFrame frame);
    // The 512 bytes after the report id; null at the limit.
    byte[]? Read(int limitMs);
}

// Enter and Exit on one thread. Enter false: busy, or unavailable (then Problem says why).
internal interface IHubLock{string? Problem{get;}bool Enter(int limitMs);void Exit();}
