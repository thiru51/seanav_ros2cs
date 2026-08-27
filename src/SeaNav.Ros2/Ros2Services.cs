using System;
using System.Runtime.InteropServices;
using SeaNav.Ros2.Native;

namespace SeaNav.Ros2
{
    // Services: the request-and-reply half of ROS 2.
    //
    // Topics are fire-and-forget. Services are a question and an answer - "reset
    // the simulation", "give me the current scenario", "set this parameter" -
    // and they are what ros2 service call talks to.
    //
    // ---------------------------------------------------------------------
    // Why this file looks different from the publisher
    // ---------------------------------------------------------------------
    //
    // Publishing has a shortcut: rcl_publish_serialized_message takes the wire
    // bytes directly, so we hand over what CdrWriter produced and never build a
    // native struct at all.
    //
    // Services have no such door. rcl_send_request wants a pointer to a real C
    // struct, and there is no _serialized version of it. That looked at first
    // like a wall, and this project documented it as one for a while.
    //
    // It is not, and the way through is worth understanding because it is what
    // keeps this library free of generated C:
    //
    //   1. Every message package exports pkg__srv__Name_Request__create(), so
    //      the native struct can be ALLOCATED by name - no generated code.
    //   2. rmw_deserialize() fills that struct from CDR bytes in ONE call - no
    //      field-by-field marshalling.
    //   3. rmw_serialize() converts the reply back to CDR the same way.
    //
    // So a service call costs two conversions rather than one, and still no code
    // generator and no per-type native library. The conversions are done by the
    // middleware's own routines, which are the same ones a C++ node uses.

    /// <summary>
    /// Calls a service and waits for the answer.
    /// </summary>
    /// <remarks>
    /// <para>Give it the service type - <c>example_interfaces/srv/AddTwoInts</c> -
    /// and it works out everything else from that name.</para>
    ///
    /// <para><b>Requests are matched to replies by sequence number</b>, not by
    /// arrival order. Send two and the answers can come back the other way
    /// round, which is why <see cref="Call"/> hands you the number it used and
    /// <see cref="TryTakeResponse"/> tells you which one arrived.</para>
    /// </remarks>
    public sealed class Ros2Client : IDisposable
    {
        private readonly Ros2Node _node;
        private readonly RclInterop.Client[] _client = new RclInterop.Client[1];
        private GCHandle _pin;

        /// <summary>Address of the native rcl handle, for a wait set. Pinned above.</summary>
        internal IntPtr NativeHandle => _pin.AddrOfPinnedObject();

        private readonly IntPtr _requestTypeSupport;
        private readonly IntPtr _responseTypeSupport;
        private readonly IntPtr _nativeRequest;
        private readonly IntPtr _nativeResponse;

        private RclInterop.SerializedMessage _scratch;
        private IntPtr _scratchBuffer;
        private int _scratchSize;

        private bool _disposed;

        /// <summary>The service type, e.g. <c>example_interfaces/srv/AddTwoInts</c>.</summary>
        public string ServiceType { get; }

        /// <summary>The service name, e.g. <c>/add_two_ints</c>.</summary>
        public string ServiceName { get; }

        /// <summary>Sequence number of the most recent request.</summary>
        public long LastSequenceNumber { get; private set; }

        internal Ros2Client(Ros2Node node, string serviceType, string serviceName, QosProfile qos)
        {
            _node = node;
            ServiceType = serviceType;
            ServiceName = serviceName;

            IntPtr typeSupport = RclInterop.ServiceTypeSupport(serviceType);
            _requestTypeSupport = RclInterop.ServiceHalfTypeSupport(serviceType, request: true);
            _responseTypeSupport = RclInterop.ServiceHalfTypeSupport(serviceType, request: false);

            // Allocated once and reused. A service call should not allocate.
            _nativeRequest = RclInterop.CreateServiceMessage(serviceType, request: true);
            _nativeResponse = RclInterop.CreateServiceMessage(serviceType, request: false);

            _pin = GCHandle.Alloc(_client, GCHandleType.Pinned);
            _client[0] = RclInterop.Fn<RclInterop.GetZeroInitializedClientFn>(
                "rcl_get_zero_initialized_client")();

            RclInterop.EndpointOptions128 options =
                RclInterop.Fn<RclInterop.ClientGetDefaultOptionsFn>("rcl_client_get_default_options")();
            if (qos != null) qos.CopyInto(ref options);

            RclInterop.Check(
                RclInterop.Fn<RclInterop.ClientInitFn>("rcl_client_init")(
                    ref _client[0], ref node.Handle, typeSupport, serviceName, ref options),
                "Creating a client for '" + serviceName + "' (" + serviceType + ")");

            _scratch = new RclInterop.SerializedMessage { Allocator = node.Context.Allocator };
        }

        /// <summary>
        /// Sends a request. Returns the sequence number that identifies it.
        /// </summary>
        /// <param name="requestCdr">The request, encoded by CdrWriter.</param>
        public long Call(byte[] requestCdr)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Client));
            if (requestCdr == null) throw new ArgumentNullException(nameof(requestCdr));

            // CDR bytes -> native struct, in one call.
            ToNative(requestCdr, _requestTypeSupport, _nativeRequest);

            long sequence;
            RclInterop.Check(
                RclInterop.Fn<RclInterop.SendRequestFn>("rcl_send_request")(
                    ref _client[0], _nativeRequest, out sequence),
                "Calling '" + ServiceName + "'");

            LastSequenceNumber = sequence;
            return sequence;
        }

        /// <summary>
        /// Checks for a reply. Returns false straight away if none has arrived.
        /// </summary>
        /// <param name="responseCdr">The reply, ready for CdrReader.</param>
        /// <param name="sequenceNumber">Which request it answers.</param>
        public bool TryTakeResponse(out byte[] responseCdr, out long sequenceNumber)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Client));

            responseCdr = null;
            sequenceNumber = 0;

            var header = new RclInterop.RequestId();
            int result = RclInterop.Fn<RclInterop.TakeResponseFn>("rcl_take_response")(
                ref _client[0], ref header, _nativeResponse);

            if (result == RclInterop.ClientTakeFailed || result == RclInterop.SubscriptionTakeFailed)
            {
                RclInterop.ClearError();
                return false;
            }

            RclInterop.Check(result, "Taking a reply from '" + ServiceName + "'");

            sequenceNumber = header.SequenceNumber;
            responseCdr = ToCdr(_nativeResponse, _responseTypeSupport);
            return true;
        }

        /// <summary>
        /// Calls and waits, polling until the reply arrives or the time runs out.
        /// </summary>
        /// <remarks>
        /// Polling rather than blocking on a wait set, for the same reason the
        /// subscription polls: a simulator already has a loop. If you need to
        /// block properly, put the client or service in a Ros2WaitSet.
        /// </remarks>
        /// <returns>The reply, or null if nothing came back in time.</returns>
        public byte[] CallAndWait(byte[] requestCdr, double timeoutSeconds = 5.0)
        {
            long sent = Call(requestCdr);
            DateTime giveUp = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < giveUp)
            {
                byte[] reply;
                long sequence;
                if (TryTakeResponse(out reply, out sequence) && sequence == sent)
                    return reply;

                System.Threading.Thread.Sleep(1);
            }
            return null;
        }

        // --- the two conversions -------------------------------------------

        /// <summary>CDR bytes into a native struct, via the middleware's own routine.</summary>
        private void ToNative(byte[] cdr, IntPtr typeSupport, IntPtr native)
        {
            EnsureScratch(cdr.Length);
            Marshal.Copy(cdr, 0, _scratchBuffer, cdr.Length);
            _scratch.Length = (UIntPtr)cdr.Length;

            RclInterop.Check(
                RclInterop.RmwFn<RclInterop.DeserializeFn>("rmw_deserialize")(
                    ref _scratch, typeSupport, native),
                "Converting a message into rcl's own representation");
        }

        /// <summary>A native struct back into CDR bytes.</summary>
        private byte[] ToCdr(IntPtr native, IntPtr typeSupport)
        {
            // rmw_serialize grows the buffer through the allocator if it needs to,
            // so start generous and let it.
            EnsureScratch(4096);
            _scratch.Length = UIntPtr.Zero;

            RclInterop.Check(
                RclInterop.RmwFn<RclInterop.SerializeFn>("rmw_serialize")(
                    native, typeSupport, ref _scratch),
                "Converting a reply back into wire format");

            var bytes = new byte[(int)_scratch.Length];
            Marshal.Copy(_scratch.Buffer, bytes, 0, bytes.Length);
            return bytes;
        }

        private void EnsureScratch(int needed)
        {
            if (needed <= _scratchSize) return;

            int size = _scratchSize == 0 ? Math.Max(needed, 4096) : _scratchSize;
            while (size < needed) size *= 2;

            IntPtr bigger = Marshal.AllocHGlobal(size);
            if (_scratchBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_scratchBuffer);

            _scratchBuffer = bigger;
            _scratchSize = size;
            _scratch.Buffer = _scratchBuffer;
            _scratch.Capacity = (UIntPtr)size;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RclInterop.Fn<RclInterop.ClientFiniFn>("rcl_client_fini")(ref _client[0], ref _node.Handle);
            if (_pin.IsAllocated) _pin.Free();

            RclInterop.DestroyServiceMessage(ServiceType, true, _nativeRequest);
            RclInterop.DestroyServiceMessage(ServiceType, false, _nativeResponse);

            if (_scratchBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_scratchBuffer);
                _scratchBuffer = IntPtr.Zero;
            }

            _node.ChildWasDisposed(this);
        }
    }

    /// <summary>
    /// Answers a service. The other side of <see cref="Ros2Client"/>.
    /// </summary>
    /// <remarks>
    /// You poll it for requests and send replies yourself, rather than
    /// registering a callback. In a simulator that is usually what you want -
    /// "reset the episode" should happen at a step boundary, not halfway through
    /// one, and a callback fired from a middleware thread cannot promise that.
    /// </remarks>
    public sealed class Ros2Service : IDisposable
    {
        private readonly Ros2Node _node;
        private readonly RclInterop.Service[] _service = new RclInterop.Service[1];
        private GCHandle _pin;

        /// <summary>Address of the native rcl handle, for a wait set. Pinned above.</summary>
        internal IntPtr NativeHandle => _pin.AddrOfPinnedObject();

        private readonly IntPtr _requestTypeSupport;
        private readonly IntPtr _responseTypeSupport;
        private readonly IntPtr _nativeRequest;
        private readonly IntPtr _nativeResponse;

        private RclInterop.SerializedMessage _scratch;
        private IntPtr _scratchBuffer;
        private int _scratchSize;

        private RclInterop.RequestId _pendingHeader;
        private bool _havePending;
        private bool _disposed;

        /// <summary>The service type.</summary>
        public string ServiceType { get; }

        /// <summary>The service name.</summary>
        public string ServiceName { get; }

        /// <summary>Requests answered so far.</summary>
        public long Answered { get; private set; }

        internal Ros2Service(Ros2Node node, string serviceType, string serviceName, QosProfile qos)
        {
            _node = node;
            ServiceType = serviceType;
            ServiceName = serviceName;

            IntPtr typeSupport = RclInterop.ServiceTypeSupport(serviceType);
            _requestTypeSupport = RclInterop.ServiceHalfTypeSupport(serviceType, request: true);
            _responseTypeSupport = RclInterop.ServiceHalfTypeSupport(serviceType, request: false);

            _nativeRequest = RclInterop.CreateServiceMessage(serviceType, request: true);
            _nativeResponse = RclInterop.CreateServiceMessage(serviceType, request: false);

            _pin = GCHandle.Alloc(_service, GCHandleType.Pinned);
            _service[0] = RclInterop.Fn<RclInterop.GetZeroInitializedServiceFn>(
                "rcl_get_zero_initialized_service")();

            RclInterop.EndpointOptions128 options =
                RclInterop.Fn<RclInterop.ServiceGetDefaultOptionsFn>(
                    "rcl_service_get_default_options")();
            if (qos != null) qos.CopyInto(ref options);

            RclInterop.Check(
                RclInterop.Fn<RclInterop.ServiceInitFn>("rcl_service_init")(
                    ref _service[0], ref node.Handle, typeSupport, serviceName, ref options),
                "Offering '" + serviceName + "' (" + serviceType + ")");

            _scratch = new RclInterop.SerializedMessage { Allocator = node.Context.Allocator };
        }

        /// <summary>
        /// Checks for a request. Returns false if nobody has called.
        /// </summary>
        /// <remarks>
        /// The caller's identity is remembered internally, so
        /// <see cref="Respond"/> knows where to send the reply. Answer one
        /// request before taking the next.
        /// </remarks>
        public bool TryTakeRequest(out byte[] requestCdr)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Service));

            requestCdr = null;

            var header = new RclInterop.RequestId();
            int result = RclInterop.Fn<RclInterop.TakeRequestFn>("rcl_take_request")(
                ref _service[0], ref header, _nativeRequest);

            if (result == RclInterop.ServiceTakeFailed || result == RclInterop.SubscriptionTakeFailed)
            {
                RclInterop.ClearError();
                return false;
            }

            RclInterop.Check(result, "Taking a request on '" + ServiceName + "'");

            _pendingHeader = header;
            _havePending = true;
            requestCdr = ToCdr(_nativeRequest, _requestTypeSupport);
            return true;
        }

        /// <summary>Sends the reply to whoever last called.</summary>
        public void Respond(byte[] responseCdr)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ros2Service));
            if (responseCdr == null) throw new ArgumentNullException(nameof(responseCdr));

            if (!_havePending)
                throw new InvalidOperationException(
                    "Respond called with no request outstanding. Take a request first - " +
                    "the reply has to be addressed to whoever asked.");

            ToNative(responseCdr, _responseTypeSupport, _nativeResponse);

            RclInterop.Check(
                RclInterop.Fn<RclInterop.SendResponseFn>("rcl_send_response")(
                    ref _service[0], ref _pendingHeader, _nativeResponse),
                "Replying on '" + ServiceName + "'");

            _havePending = false;
            Answered++;
        }

        // The same two conversions as the client, in the opposite order.

        private void ToNative(byte[] cdr, IntPtr typeSupport, IntPtr native)
        {
            EnsureScratch(cdr.Length);
            Marshal.Copy(cdr, 0, _scratchBuffer, cdr.Length);
            _scratch.Length = (UIntPtr)cdr.Length;

            RclInterop.Check(
                RclInterop.RmwFn<RclInterop.DeserializeFn>("rmw_deserialize")(
                    ref _scratch, typeSupport, native),
                "Converting a message into rcl's own representation");
        }

        private byte[] ToCdr(IntPtr native, IntPtr typeSupport)
        {
            EnsureScratch(4096);
            _scratch.Length = UIntPtr.Zero;

            RclInterop.Check(
                RclInterop.RmwFn<RclInterop.SerializeFn>("rmw_serialize")(
                    native, typeSupport, ref _scratch),
                "Converting a request into wire format");

            var bytes = new byte[(int)_scratch.Length];
            Marshal.Copy(_scratch.Buffer, bytes, 0, bytes.Length);
            return bytes;
        }

        private void EnsureScratch(int needed)
        {
            if (needed <= _scratchSize) return;

            int size = _scratchSize == 0 ? Math.Max(needed, 4096) : _scratchSize;
            while (size < needed) size *= 2;

            IntPtr bigger = Marshal.AllocHGlobal(size);
            if (_scratchBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_scratchBuffer);

            _scratchBuffer = bigger;
            _scratchSize = size;
            _scratch.Buffer = _scratchBuffer;
            _scratch.Capacity = (UIntPtr)size;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RclInterop.Fn<RclInterop.ServiceFiniFn>("rcl_service_fini")(
                ref _service[0], ref _node.Handle);
            if (_pin.IsAllocated) _pin.Free();

            RclInterop.DestroyServiceMessage(ServiceType, true, _nativeRequest);
            RclInterop.DestroyServiceMessage(ServiceType, false, _nativeResponse);

            if (_scratchBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_scratchBuffer);
                _scratchBuffer = IntPtr.Zero;
            }

            _node.ChildWasDisposed(this);
        }
    }
}
