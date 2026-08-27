using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SeaNav.Ros2.Native;

namespace SeaNav.Ros2
{
    // The friendly layer. Everything below this file is pointers and structs;
    // everything here is ordinary C# you can read.
    //
    // The shape mirrors ROS itself:
    //
    //     Ros2Context   ->  starts and stops ROS for this process (one of these)
    //       Ros2Node    ->  a named participant other nodes can see
    //         Ros2Publisher  ->  sends messages on one topic
    //
    // Dispose the context and it cleans up everything under it, so a `using`
    // block is usually all the lifetime management you need.

    /// <summary>
    /// Starts ROS 2 for this process and shuts it down again. Make one of these
    /// before anything else, and keep it alive for as long as you're publishing.
    /// </summary>
    public sealed class Ros2Context : IDisposable
    {
        // Held in a pinned single-element array rather than as a plain field.
        //
        // This is not fussiness. rcl_node_init takes a POINTER to the context and
        // keeps it - every later call goes through that pointer. A struct sitting
        // in a field of a managed object lives on the GC heap and the collector
        // is free to move it during a compaction, at which point rcl is holding
        // an address that means nothing any more.
        //
        // The symptom is horrible: everything works, sometimes for a whole run,
        // until a collection happens at the wrong moment. Then rcl reports
        // "publisher's context is invalid" - or worse, quietly reads whatever is
        // at the old address now. Pinning the array stops it moving, for the
        // lifetime of this object.
        private readonly RclInterop.Context[] _context = new RclInterop.Context[1];
        private GCHandle _contextPin;
        private readonly List<Ros2Node> _nodes = new List<Ros2Node>();
        private bool _disposed;

        /// <summary>
        /// ROS's default memory allocator. Everything we create shares it, and
        /// we never build one ourselves - we just pass this one along.
        /// </summary>
        public RclInterop.Allocator Allocator { get; }

        /// <summary>
        /// Starts ROS 2.
        /// </summary>
        /// <param name="rosLibraryFolder">
        /// Where the ROS libraries live, e.g. "/opt/ros/jazzy/lib". You can leave
        /// this out when you've already run "source /opt/ros/jazzy/setup.bash".
        /// Inside Unity you almost certainly need to pass it, because the game
        /// doesn't get to fix its own library search path after it has started.
        /// </param>
        public Ros2Context(string rosLibraryFolder = null)
        {
            if (!string.IsNullOrEmpty(rosLibraryFolder))
                NativeLoader.SearchPath = rosLibraryFolder;

            _contextPin = GCHandle.Alloc(_context, GCHandleType.Pinned);

            Allocator = RclInterop.UtilsFn<RclInterop.GetDefaultAllocatorFn>(
                "rcutils_get_default_allocator")();

            // ROS wants a blank context and a blank options struct, then fills
            // them in. Asking for the blank ones rather than zeroing our own
            // matters: rcl checks for its own sentinel values.
            _context[0] = RclInterop.Fn<RclInterop.GetZeroInitializedContextFn>(
                "rcl_get_zero_initialized_context")();

            RclInterop.InitOptions options = RclInterop.Fn<RclInterop.GetZeroInitializedInitOptionsFn>(
                "rcl_get_zero_initialized_init_options")();

            RclInterop.Check(
                RclInterop.Fn<RclInterop.InitOptionsInitFn>("rcl_init_options_init")(ref options, Allocator),
                "Setting up ROS init options");

            try
            {
                // argc/argv would let ROS parse command line arguments like
                // __ns:= remappings. We pass none; add them here if you need them.
                RclInterop.Check(
                    RclInterop.Fn<RclInterop.InitFn>("rcl_init")(0, IntPtr.Zero, ref options, ref _context[0]),
                    "Starting ROS 2");
            }
            finally
            {
                // rcl_init copies what it needs, so the options are ours to free
                // either way - including when init failed.
                RclInterop.Fn<RclInterop.InitOptionsFiniFn>("rcl_init_options_fini")(ref options);
            }
        }

        /// <summary>
        /// True while ROS is running. Goes false after Dispose, or if something
        /// else shut ROS down (Ctrl+C, for instance).
        /// </summary>
        public bool Ok
        {
            get
            {
                if (_disposed) return false;
                return RclInterop.Fn<RclInterop.ContextIsValidFn>("rcl_context_is_valid")(ref _context[0]);
            }
        }

        /// <summary>
        /// Creates a node. This is what shows up in "ros2 node list".
        /// </summary>
        /// <param name="name">Node name, no leading slash. Letters, digits and underscores.</param>
        /// <param name="nameSpace">Optional namespace to group nodes under.</param>
        public Ros2Node CreateNode(string name, string nameSpace = "")
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Context));

            Ros2Node node = new Ros2Node(this, name, nameSpace);
            _nodes.Add(node);
            return node;
        }

        // Handed to rcl functions that need to write into our context.
        internal ref RclInterop.Context Handle => ref _context[0];

        internal void NodeWasDisposed(Ros2Node node)
        {
            _nodes.Remove(node);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Nodes hold handles that point into this context, so they have to go
            // first. We copy the list because disposing a node removes it from ours
            // and you can't modify a list while walking it.
            foreach (Ros2Node node in _nodes.ToArray())
                node.Dispose();
            _nodes.Clear();

            RclInterop.Fn<RclInterop.ShutdownFn>("rcl_shutdown")(ref _context[0]);
            RclInterop.Fn<RclInterop.ContextFiniFn>("rcl_context_fini")(ref _context[0]);

            // Only release the pin once rcl has finished with the memory.
            if (_contextPin.IsAllocated) _contextPin.Free();
        }
    }

    /// <summary>
    /// A ROS 2 node. Nodes own publishers and subscriptions, and they're the
    /// thing other programs on the network discover.
    /// </summary>
    public sealed class Ros2Node : IDisposable
    {
        private readonly Ros2Context _context;

        // Pinned for the same reason the context is: rcl_publisher_init stores a
        // pointer to the node and dereferences it on every publish.
        private readonly RclInterop.Node[] _node = new RclInterop.Node[1];
        private GCHandle _nodePin;
        private readonly List<IDisposable> _children = new List<IDisposable>();
        private bool _disposed;

        /// <summary>The name you gave it.</summary>
        public string Name { get; }

        /// <summary>Its namespace, or empty string for none.</summary>
        public string Namespace { get; }

        internal Ros2Node(Ros2Context context, string name, string nameSpace)
        {
            _context = context;
            Name = name;
            Namespace = nameSpace ?? string.Empty;

            _nodePin = GCHandle.Alloc(_node, GCHandleType.Pinned);
            _node[0] = RclInterop.Fn<RclInterop.GetZeroInitializedNodeFn>("rcl_get_zero_initialized_node")();
            RclInterop.NodeOptions options =
                RclInterop.Fn<RclInterop.NodeGetDefaultOptionsFn>("rcl_node_get_default_options")();

            RclInterop.Check(
                RclInterop.Fn<RclInterop.NodeInitFn>("rcl_node_init")(
                    ref _node[0], name, Namespace, ref context.Handle, ref options),
                "Creating node '" + name + "'");
        }

        /// <summary>
        /// Creates a publisher for one topic.
        /// </summary>
        /// <param name="messageType">Full ROS type name, e.g. "sensor_msgs/msg/Imu".</param>
        /// <param name="topic">Topic name. Start it with "/" to ignore the namespace.</param>
        /// <param name="qos">
        /// Delivery settings. Leave null for ROS defaults, or use QosProfile.SensorData
        /// for anything high-rate. Worth getting right - see the notes on QosProfile.
        /// </param>
        public Ros2Publisher CreatePublisher(string messageType, string topic, QosProfile qos = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Node));

            Ros2Publisher publisher = new Ros2Publisher(this, messageType, topic, qos);
            _children.Add(publisher);
            return publisher;
        }

        /// <summary>
        /// Subscribes to a topic. You get the raw CDR bytes back and decode them
        /// yourself with CdrReader - same deal as publishing, in reverse.
        /// </summary>
        /// <param name="messageType">Full ROS type name, e.g. "geometry_msgs/msg/Twist".</param>
        /// <param name="topic">Topic to listen on.</param>
        /// <param name="qos">
        /// Must be compatible with whoever is publishing. If you subscribe with
        /// the default (reliable) to a sensor topic published best-effort, you
        /// will receive absolutely nothing and be told absolutely nothing.
        /// </param>
        public Ros2Subscription CreateSubscription(string messageType, string topic, QosProfile qos = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Node));

            Ros2Subscription subscription = new Ros2Subscription(this, messageType, topic, qos);
            _children.Add(subscription);
            return subscription;
        }

        internal ref RclInterop.Node Handle => ref _node[0];

        internal Ros2Context Context => _context;

        internal void ChildWasDisposed(IDisposable child)
        {
            _children.Remove(child);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (IDisposable child in _children.ToArray())
                child.Dispose();
            _children.Clear();

            RclInterop.Fn<RclInterop.NodeFiniFn>("rcl_node_fini")(ref _node[0]);
            if (_nodePin.IsAllocated) _nodePin.Free();
            _context.NodeWasDisposed(this);
        }
    }

    /// <summary>
    /// Publishes messages on a topic.
    /// </summary>
    /// <remarks>
    /// You hand this class bytes, not objects. The bytes are CDR - the format
    /// ROS uses on the wire - and SeaNav.Core.Ros.CdrWriter produces them.
    ///
    /// Most C# ROS libraries work the other way round: you fill in a generated
    /// message class, and the library copies it field by field into a C struct,
    /// which the DDS layer then walks again to produce those same CDR bytes.
    /// That works fine and it isn't slow, but it needs a code generator and a
    /// small compiled library for every message type in existence.
    ///
    /// Publishing the bytes directly skips all of that. The trade-off is that
    /// this route covers topics only. Services can't work this way, because rcl
    /// has no "send this request as raw bytes" function - a service call needs
    /// the real C struct.
    /// </remarks>
    public sealed class Ros2Publisher : IDisposable
    {
        private readonly Ros2Node _node;
        private readonly RclInterop.Publisher[] _publisher = new RclInterop.Publisher[1];
        private GCHandle _publisherPin;
        private RclInterop.SerializedMessage _message;

        // We keep one unmanaged buffer and reuse it. Publishing at 200 Hz should
        // not allocate, so the buffer only grows and never shrinks.
        private IntPtr _buffer;
        private int _bufferSize;

        private bool _disposed;

        // Set once ROS has shut down under us, so we stop trying rather than
        // throwing the same exception on every subsequent call.
        private bool _shutDown;

        /// <summary>The message type, e.g. "sensor_msgs/msg/Imu".</summary>
        public string MessageType { get; }

        /// <summary>The topic name, as you gave it.</summary>
        public string Topic { get; }

        /// <summary>How many messages have actually gone out.</summary>
        public long Published { get; private set; }

        /// <summary>
        /// True once ROS shut down underneath this publisher - normally because
        /// someone pressed Ctrl+C. Publishing after that does nothing.
        /// </summary>
        public bool ShutDown { get { return _shutDown; } }

        internal Ros2Publisher(Ros2Node node, string messageType, string topic, QosProfile qos)
        {
            _node = node;
            MessageType = messageType;
            Topic = topic;

            IntPtr typeSupport = RclInterop.MessageTypeSupport(messageType);

            _publisherPin = GCHandle.Alloc(_publisher, GCHandleType.Pinned);
            _publisher[0] = RclInterop.Fn<RclInterop.GetZeroInitializedPublisherFn>(
                "rcl_get_zero_initialized_publisher")();

            RclInterop.PublisherOptions options =
                RclInterop.Fn<RclInterop.PublisherGetDefaultOptionsFn>("rcl_publisher_get_default_options")();

            // Start from ROS's defaults and change only what the caller asked for.
            if (qos != null)
                qos.CopyInto(ref options);

            RclInterop.Check(
                RclInterop.Fn<RclInterop.PublisherInitFn>("rcl_publisher_init")(
                    ref _publisher[0], ref node.Handle, typeSupport, topic, ref options),
                "Creating publisher on '" + topic + "' for " + messageType);

            _message = new RclInterop.SerializedMessage { Allocator = node.Context.Allocator };
        }

        /// <summary>Sends one message.</summary>
        /// <param name="cdrBytes">
        /// The encoded message, including its four-byte encapsulation header.
        /// RosCdr.Serialise gives you exactly this.
        /// </param>
        public void Publish(byte[] cdrBytes)
        {
            if (cdrBytes == null) throw new ArgumentNullException(nameof(cdrBytes));
            Publish(cdrBytes, cdrBytes.Length);
        }

        /// <summary>
        /// Sends the first <paramref name="length"/> bytes of a buffer. Useful if
        /// you encode into a bigger scratch array and don't want to trim it.
        /// </summary>
        public void Publish(byte[] cdrBytes, int length)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Publisher));
            if (cdrBytes == null) throw new ArgumentNullException(nameof(cdrBytes));

            if (length < 4 || length > cdrBytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length),
                    "A CDR message is at least 4 bytes - that's the encapsulation header on its own.");
            }

            if (_shutDown) return;

            MakeRoomFor(length);
            Marshal.Copy(cdrBytes, 0, _buffer, length);
            _message.Length = (UIntPtr)length;

            int result = RclInterop.Fn<RclInterop.PublishSerializedFn>("rcl_publish_serialized_message")(
                ref _publisher[0], ref _message, IntPtr.Zero);

            if (result == 0)
            {
                Published++;
                return;
            }

            // Ctrl+C is not a failure. ROS installs its own signal handler when
            // you call rcl_init, and it shuts the context down underneath you -
            // so a publish already in flight comes back saying the publisher is
            // invalid. Throwing there means a perfectly ordinary Ctrl+C ends in
            // an unhandled exception and a core dump, which is a rotten way to
            // stop a simulator.
            //
            // Anything else really is a failure and still throws.
            if (!_node.Context.Ok)
            {
                _shutDown = true;
                RclInterop.ClearError();
                return;
            }

            RclInterop.Check(result, "Publishing on '" + Topic + "'");
        }

        // Grows the unmanaged buffer by doubling, so repeated publishing settles
        // on one allocation rather than reallocating every time.
        private void MakeRoomFor(int bytes)
        {
            if (bytes <= _bufferSize) return;

            int newSize = _bufferSize == 0 ? Math.Max(bytes, 256) : _bufferSize;
            while (newSize < bytes)
                newSize *= 2;

            IntPtr bigger = Marshal.AllocHGlobal(newSize);
            if (_buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_buffer);

            _buffer = bigger;
            _bufferSize = newSize;

            _message.Buffer = _buffer;
            _message.Capacity = (UIntPtr)newSize;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RclInterop.Fn<RclInterop.PublisherFiniFn>("rcl_publisher_fini")(
                ref _publisher[0], ref _node.Handle);
            if (_publisherPin.IsAllocated) _publisherPin.Free();

            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }

            _node.ChildWasDisposed(this);
        }
    }

    /// <summary>
    /// Listens on a topic and hands you the raw message bytes.
    /// </summary>
    /// <remarks>
    /// This does not call you back. You ask it whether anything has arrived, and
    /// it tells you. That suits a simulator, where you already have a loop
    /// running at a fixed rate and want to read commands once per step.
    ///
    /// The cost is that a tight loop calling TryTake() with nothing to read will
    /// spin the CPU. If you ever need to block until a message arrives, that is
    /// what rcl's wait sets are for - rcl_wait_set_init and friends. Not wired up
    /// here yet, because nothing in SEANAV needs it.
    /// </remarks>
    public sealed class Ros2Subscription : IDisposable
    {
        private readonly Ros2Node _node;
        private readonly RclInterop.Subscription[] _subscription = new RclInterop.Subscription[1];
        private GCHandle _subscriptionPin;
        private RclInterop.SerializedMessage _incoming;
        private bool _disposed;

        /// <summary>The message type we're listening for.</summary>
        public string MessageType { get; }

        /// <summary>The topic name, as you gave it.</summary>
        public string Topic { get; }

        /// <summary>How many messages TryTake has handed back so far.</summary>
        public long Received { get; private set; }

        internal Ros2Subscription(Ros2Node node, string messageType, string topic, QosProfile qos)
        {
            _node = node;
            MessageType = messageType;
            Topic = topic;

            IntPtr typeSupport = RclInterop.MessageTypeSupport(messageType);

            _subscriptionPin = GCHandle.Alloc(_subscription, GCHandleType.Pinned);
            _subscription[0] = RclInterop.Fn<RclInterop.GetZeroInitializedSubscriptionFn>(
                "rcl_get_zero_initialized_subscription")();

            RclInterop.SubscriptionOptions options =
                RclInterop.Fn<RclInterop.SubscriptionGetDefaultOptionsFn>(
                    "rcl_subscription_get_default_options")();

            if (qos != null)
                qos.CopyInto(ref options);

            RclInterop.Check(
                RclInterop.Fn<RclInterop.SubscriptionInitFn>("rcl_subscription_init")(
                    ref _subscription[0], ref node.Handle, typeSupport, topic, ref options),
                "Subscribing to '" + topic + "' for " + messageType);

            // rcl fills this buffer in for us and will grow it through the
            // allocator if a message doesn't fit, so we let rcutils set it up
            // rather than handing over memory we allocated ourselves.
            _incoming = new RclInterop.SerializedMessage();
            RclInterop.Allocator allocator = node.Context.Allocator;
            RclInterop.Check(
                RclInterop.UtilsFn<RclInterop.Uint8ArrayInitFn>("rcutils_uint8_array_init")(
                    ref _incoming, (UIntPtr)1024, ref allocator),
                "Allocating the receive buffer");
        }

        /// <summary>
        /// Checks for a message. Returns false straight away if nothing is waiting.
        /// </summary>
        /// <param name="cdrBytes">
        /// The message as it came off the wire, ready for CdrReader. Only valid
        /// when this returns true.
        /// </param>
        public bool TryTake(out byte[] cdrBytes)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Subscription));

            cdrBytes = null;

            int result = RclInterop.Fn<RclInterop.TakeSerializedFn>("rcl_take_serialized_message")(
                ref _subscription[0], ref _incoming, IntPtr.Zero, IntPtr.Zero);

            // 401 just means the queue was empty. Anything else is a real problem.
            if (result == RclInterop.SubscriptionTakeFailed)
            {
                // rcl records an error string even for this, and if we leave it
                // there the next genuine failure reports this stale message instead.
                RclInterop.ClearError();
                return false;
            }

            RclInterop.Check(result, "Taking a message from '" + Topic + "'");

            int length = (int)_incoming.Length;
            cdrBytes = new byte[length];
            Marshal.Copy(_incoming.Buffer, cdrBytes, 0, length);

            Received++;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RclInterop.Fn<RclInterop.SubscriptionFiniFn>("rcl_subscription_fini")(
                ref _subscription[0], ref _node.Handle);
            if (_subscriptionPin.IsAllocated) _subscriptionPin.Free();

            RclInterop.UtilsFn<RclInterop.Uint8ArrayFiniFn>("rcutils_uint8_array_fini")(ref _incoming);

            _node.ChildWasDisposed(this);
        }
    }
}
