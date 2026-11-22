using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Dispatching;

namespace DeadWare.WinUI;

/// <summary>
/// One value the large keyboard view asks the host for (the painted set, the layer list), sent whole: the asked value shows at once in
/// place of the state's (<see cref="HeldValue{T}"/>), changes go through a <see cref="Throttle"/> (at most once per 100 ms, the last one
/// kept), and a reply from a client the view no longer follows or from an earlier opening counts for nothing. A refusal drops the asked
/// value and any change still waiting, so the state's value shows again; the page's send shows the refusal in the status line.
/// </summary>
/// <typeparam name="T">The value: a painted set or a layer list. Null is a value too ("nothing painted", "no layers").</typeparam>
internal sealed class HeldRequest<T> where T : class
{
    private readonly HeldValue<T> held = new();
    private readonly Throttle sends;
    private readonly Action<HostMessage, Action<TrackedReply>?> send;
    private readonly Action replied;
    private readonly Func<bool> waitingElsewhere;
    private int opening;
    private long shown;

    /// <param name="queue">The page's queue, for the throttle.</param>
    /// <param name="send">The page's tracked send.</param>
    /// <param name="replied">Told after each reply of this opening has been counted, so the card shows the value now held or not.</param>
    /// <param name="waitingElsewhere">Whether a request for this value waits somewhere other than this throttle (a slider's own), which holds the asked value too.</param>
    public HeldRequest(DispatcherQueue queue, Action<HostMessage, Action<TrackedReply>?> send, Action replied, Func<bool>? waitingElsewhere = null)
    {
        this.send = send;
        this.replied = replied;
        this.waitingElsewhere = waitingElsewhere ?? (() => false);
        sends = new Throttle(queue, request => Send(request, null));
    }

    /// <summary>The value to show: the one asked for while it is held, else the state's.</summary>
    public T? Current(T? state) => held.Current(state);

    /// <summary>A change waits in the throttle for its interval.</summary>
    public bool Pending => sends.Pending;

    /// <summary>Shows <paramref name="value"/> from now on, for a request that goes another way (a slider's own throttle, then <see cref="Send"/>).</summary>
    public void Ask(T? value) => held.Ask(value);

    /// <summary>Shows <paramref name="value"/> from now on and posts its request to the throttle; <paramref name="now"/> sends it at once.</summary>
    public void Post(T? value, HostMessage request, bool now)
    {
        held.Ask(value);
        sends.Post(request);
        if (now) sends.Flush();
    }

    /// <summary>Sends a change waiting for its interval now.</summary>
    public void Flush() => sends.Flush();

    /// <summary>Drops a change waiting for its interval.</summary>
    public void Cancel() => sends.Cancel();

    /// <summary>
    /// Sends a request for this value; <paramref name="done"/> (a slider's bookkeeping) gets every reply, even one from an earlier opening.
    /// </summary>
    public void Send(HostMessage request, Action<Reply>? done)
    {
        int ticket = opening;
        held.Sent();
        send(request, tracked =>
        {
            done?.Invoke(tracked.Reply);
            if (ticket != opening) return;
            // A change still waiting was made over the refused one; the next change sends again.
            if (!tracked.Reply.Ok) sends.Cancel();
            held.Replied(tracked.Reply.Ok, tracked.StatesBefore);
            Settle();
            replied();
        });
    }

    /// <summary>The page's shown state count, as a state is shown: the asked value goes once that state carries it.</summary>
    public void Shown(long seq)
    {
        shown = seq;
        Settle();
    }

    /// <summary>
    /// Ends an opening: a change waiting goes now with <paramref name="sendWaiting"/> or is dropped, nothing is held any more, and late
    /// replies count for nothing.
    /// </summary>
    public void Reset(bool sendWaiting)
    {
        if (sendWaiting) sends.Flush();
        else sends.Cancel();
        opening++;
        held.Reset();
        shown = 0;
    }

    private void Settle() => held.Settle(shown, sends.Pending || waitingElsewhere());
}
