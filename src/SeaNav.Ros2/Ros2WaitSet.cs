using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SeaNav.Ros2.Native;

namespace SeaNav.Ros2
{
    /// <summary>
    /// Sleeps until one of the things you put in it has something to offer.
    /// </summary>
    /// <remarks>
    /// <para><b>The problem this solves.</b> Everything else in this library
    /// polls: you call <c>TryTake</c>, and it tells you whether anything arrived.
    /// That suits a simulator, which already runs a loop at a fixed rate and
    /// wants to read commands once per step.</para>
    ///
    /// <para>It suits nothing else. A program whose only job is to listen has no
    /// natural rate, so it ends up either sleeping — and adding latency it did not
    /// need — or spinning, and burning a whole CPU core asking "anything yet?"
    /// millions of times a second. Neither is acceptable for a node that might
    /// run for days.</para>
    ///
    /// <para>A wait set is the answer: hand the middleware everything you care
    /// about, and it puts the thread to sleep until one of them is ready or your
    /// timeout expires. No latency, no spinning.</para>
    ///
    /// <code>
    /// using (var wait = new Ros2WaitSet(context))
    /// {
    ///     wait.Add(subscription);
    ///     wait.Add(service);
    ///
    ///     while (running)
    ///     {
    ///         if (!wait.Wait(1.0)) continue;              // nothing in a second
    ///
    ///         if (wait.IsReady(subscription)) { /* ... */ }
    ///         if (wait.IsReady(service))      { /* ... */ }
    ///     }
    /// }
    /// </code>
    ///
    /// <para><b>Add once, wait many times.</b> The registrations are kept on this
    /// side, and re-applied on every <see cref="Wait"/>. That is not an
    /// optimisation being skipped — it is required. <c>rcl_wait</c> <i>prunes</i>
    /// the set it is given, overwriting every entry that was not ready with null,
    /// so a set is single-use and has to be refilled each time round. Doing that
    /// for you removes the single most common way this API is misused: waiting
    /// twice and wondering why the second call never reports anything.</para>
    ///
    /// <para><b>Not for Unity's main thread.</b> Blocking is the entire point, and
    /// blocking the thread that renders frames will freeze the editor. In Unity,
    /// keep polling on the main thread; use this on a background thread or in a
    /// headless process.</para>
    ///
    /// <para><b>One thread only.</b> An instance must not be waited on from two
    /// threads at once — rcl documents that as undefined behaviour, not as an
    /// error you will be told about. Give each thread its own.</para>
    /// </remarks>
    public sealed class Ros2WaitSet : IDisposable
    {
        private readonly Ros2Context _context;

        // Pinned for the same reason every other rcl handle here is: rcl keeps
        // the address, and a garbage collection that moved it would corrupt the
        // set at a moment unrelated to anything we did.
        private readonly RclInterop.WaitSet[] _waitSet = new RclInterop.WaitSet[1];
        private GCHandle _pin;
        private bool _initialised;
        private bool _disposed;

        private readonly List<Ros2Subscription> _subscriptions = new List<Ros2Subscription>();
        private readonly List<Ros2Client> _clients = new List<Ros2Client>();
        private readonly List<Ros2Service> _services = new List<Ros2Service>();

        // Which native handles came back non-null from the last wait.
        private readonly HashSet<IntPtr> _ready = new HashSet<IntPtr>();

        /// <summary>How many times Wait returned with something ready.</summary>
        public long Wakeups { get; private set; }

        /// <summary>How many times Wait returned having timed out.</summary>
        public long Timeouts { get; private set; }

        /// <summary>Creates an empty wait set. Add things, then Wait.</summary>
        public Ros2WaitSet(Ros2Context context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _pin = GCHandle.Alloc(_waitSet, GCHandleType.Pinned);
            _waitSet[0] = RclInterop.Fn<RclInterop.GetZeroInitializedWaitSetFn>(
                "rcl_get_zero_initialized_wait_set")();
        }

        /// <summary>Watch a subscription for incoming messages.</summary>
        public void Add(Ros2Subscription subscription)
        {
            if (subscription == null) throw new ArgumentNullException(nameof(subscription));
            ThrowIfDisposed();
            _subscriptions.Add(subscription);
            _initialised = false;      // the set has to be resized
        }

        /// <summary>Watch a client for the reply to a request.</summary>
        public void Add(Ros2Client client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            ThrowIfDisposed();
            _clients.Add(client);
            _initialised = false;
        }

        /// <summary>Watch a service for incoming requests.</summary>
        public void Add(Ros2Service service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            ThrowIfDisposed();
            _services.Add(service);
            _initialised = false;
        }

        /// <summary>
        /// Sleeps until something is ready, or the timeout runs out.
        /// </summary>
        /// <param name="timeoutSeconds">
        /// How long to wait. A negative number blocks forever, which is what rcl
        /// does with a negative timeout; zero returns immediately and is a plain
        /// poll.
        /// </param>
        /// <returns>
        /// True if at least one thing is ready — ask <see cref="IsReady(Ros2Subscription)"/>
        /// which. False on timeout, which is normal and not an error.
        /// </returns>
        public bool Wait(double timeoutSeconds)
        {
            ThrowIfDisposed();

            if (_subscriptions.Count == 0 && _clients.Count == 0 && _services.Count == 0)
            {
                // rcl reports WAIT_SET_EMPTY here. Sleeping forever on nothing is
                // never what anyone meant, so say so plainly instead.
                throw new InvalidOperationException(
                    "Nothing has been added to this wait set, so waiting would block " +
                    "forever with no way to be woken. Add a subscription, client or " +
                    "service first.");
            }

            EnsureInitialised();
            Refill();

            long timeout = timeoutSeconds < 0
                ? -1
                : (long)Math.Round(timeoutSeconds * 1e9);

            int result = RclInterop.Fn<RclInterop.WaitFn>("rcl_wait")(ref _waitSet[0], timeout);

            if (result == RclInterop.Timeout)
            {
                _ready.Clear();
                Timeouts++;
                return false;
            }

            RclInterop.Check(result, "rcl_wait");
            CollectReady();
            Wakeups++;
            return _ready.Count > 0;
        }

        /// <summary>Did this subscription have a message, as of the last Wait?</summary>
        public bool IsReady(Ros2Subscription subscription)
        {
            return subscription != null && _ready.Contains(subscription.NativeHandle);
        }

        /// <summary>Did this client's reply arrive, as of the last Wait?</summary>
        public bool IsReady(Ros2Client client)
        {
            return client != null && _ready.Contains(client.NativeHandle);
        }

        /// <summary>Did this service have a request, as of the last Wait?</summary>
        public bool IsReady(Ros2Service service)
        {
            return service != null && _ready.Contains(service.NativeHandle);
        }

        // --- the rcl side ----------------------------------------------------

        /// <summary>
        /// Sizes the native set to what has been added, creating it the first
        /// time and replacing it whenever the counts change.
        /// </summary>
        private void EnsureInitialised()
        {
            if (_initialised) return;

            if (_waitSet[0].Impl != IntPtr.Zero)
            {
                RclInterop.Check(
                    RclInterop.Fn<RclInterop.WaitSetFiniFn>("rcl_wait_set_fini")(ref _waitSet[0]),
                    "rcl_wait_set_fini");
            }

            RclInterop.Check(
                RclInterop.Fn<RclInterop.WaitSetInitFn>("rcl_wait_set_init")(
                    ref _waitSet[0],
                    (UIntPtr)_subscriptions.Count,
                    UIntPtr.Zero,                       // guard conditions - none used
                    UIntPtr.Zero,                       // timers - we keep our own clock
                    (UIntPtr)_clients.Count,
                    (UIntPtr)_services.Count,
                    UIntPtr.Zero,                       // events - QoS events, not wired up
                    ref _context.Handle,

                    // The context's allocator, not a fresh one. rcl_get_default_allocator
                    // does not exist - the allocator lives in rcutils - and even if it
                    // did, handing rcl a different allocator from the one that built the
                    // context is asking for a free() by the wrong owner.
                    _context.Allocator),
                "rcl_wait_set_init");

            _initialised = true;
        }

        /// <summary>
        /// Empties the native set and puts everything back into it.
        /// </summary>
        /// <remarks>
        /// Every single wait, because rcl_wait nulls out whatever was not ready.
        /// </remarks>
        private void Refill()
        {
            RclInterop.Check(
                RclInterop.Fn<RclInterop.WaitSetClearFn>("rcl_wait_set_clear")(ref _waitSet[0]),
                "rcl_wait_set_clear");

            UIntPtr index;

            foreach (Ros2Subscription s in _subscriptions)
            {
                RclInterop.Check(
                    RclInterop.Fn<RclInterop.WaitSetAddFn>("rcl_wait_set_add_subscription")(
                        ref _waitSet[0], s.NativeHandle, out index),
                    "rcl_wait_set_add_subscription");
            }

            foreach (Ros2Client c in _clients)
            {
                RclInterop.Check(
                    RclInterop.Fn<RclInterop.WaitSetAddFn>("rcl_wait_set_add_client")(
                        ref _waitSet[0], c.NativeHandle, out index),
                    "rcl_wait_set_add_client");
            }

            foreach (Ros2Service v in _services)
            {
                RclInterop.Check(
                    RclInterop.Fn<RclInterop.WaitSetAddFn>("rcl_wait_set_add_service")(
                        ref _waitSet[0], v.NativeHandle, out index),
                    "rcl_wait_set_add_service");
            }
        }

        /// <summary>
        /// Reads back which slots survived the prune.
        /// </summary>
        /// <remarks>
        /// The arrays hold pointers to the same rcl handles we put in, so a
        /// surviving entry can be matched straight against NativeHandle. Entries
        /// that were not ready are null and are skipped.
        /// </remarks>
        private void CollectReady()
        {
            _ready.Clear();
            Harvest(_waitSet[0].Subscriptions, _subscriptions.Count);
            Harvest(_waitSet[0].Clients, _clients.Count);
            Harvest(_waitSet[0].Services, _services.Count);
        }

        private void Harvest(IntPtr array, int count)
        {
            if (array == IntPtr.Zero) return;

            for (int i = 0; i < count; i++)
            {
                IntPtr entry = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                if (entry != IntPtr.Zero) _ready.Add(entry);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2WaitSet));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_waitSet[0].Impl != IntPtr.Zero)
                {
                    RclInterop.Fn<RclInterop.WaitSetFiniFn>("rcl_wait_set_fini")(ref _waitSet[0]);
                }
            }
            catch { /* shutting down; a failure here helps nobody */ }

            if (_pin.IsAllocated) _pin.Free();
        }
    }
}
