using System;
using System.Collections.Generic;

namespace SeaNav.Ros2
{
    /// <summary>
    /// The kinds of value a ROS 2 parameter can hold.
    /// </summary>
    /// <remarks>
    /// These numbers are the wire values from <c>rcl_interfaces/msg/ParameterType</c>,
    /// not an ordering of our own. <c>NotSet</c> is how ROS says a parameter does
    /// not exist, which is different from it existing with an empty value.
    /// </remarks>
    public enum ParameterType : byte
    {
        NotSet = 0,
        Bool = 1,
        Integer = 2,
        Double = 3,
        String = 4,
        ByteArray = 5,
        BoolArray = 6,
        IntegerArray = 7,
        DoubleArray = 8,
        StringArray = 9
    }

    /// <summary>One parameter: a name, a type, and a value.</summary>
    /// <remarks>
    /// Deliberately a small closed set rather than <c>object</c>. A parameter that
    /// silently changes type between reads is a horrible thing to debug, and ROS
    /// itself refuses the change by default.
    /// </remarks>
    public sealed class Parameter
    {
        public string Name { get; }
        public ParameterType Type { get; private set; }

        public bool BoolValue { get; private set; }
        public long IntegerValue { get; private set; }
        public double DoubleValue { get; private set; }
        public string StringValue { get; private set; } = string.Empty;

        /// <summary>Human-readable note, returned by <c>describe_parameters</c>.</summary>
        public string Description = string.Empty;

        /// <summary>When true, a set attempt is refused with a reason.</summary>
        public bool ReadOnly;

        private Parameter(string name) { Name = name; Type = ParameterType.NotSet; }

        /// <summary>
        /// A parameter that does not exist. ROS answers a request for an unknown
        /// name with this rather than an error, so a caller asking for five and
        /// getting four still learns which one was missing.
        /// </summary>
        public static Parameter NotSetNamed(string name) => new Parameter(name);

        public Parameter(string name, bool value) { Name = name; Set(value); }
        public Parameter(string name, long value) { Name = name; Set(value); }
        public Parameter(string name, double value) { Name = name; Set(value); }
        public Parameter(string name, string value) { Name = name; Set(value); }

        public void Set(bool value) { Type = ParameterType.Bool; BoolValue = value; }
        public void Set(long value) { Type = ParameterType.Integer; IntegerValue = value; }
        public void Set(double value) { Type = ParameterType.Double; DoubleValue = value; }

        public void Set(string value)
        {
            Type = ParameterType.String;
            StringValue = value ?? string.Empty;
        }

        public override string ToString()
        {
            switch (Type)
            {
                case ParameterType.Bool: return Name + " = " + BoolValue;
                case ParameterType.Integer: return Name + " = " + IntegerValue;
                case ParameterType.Double: return Name + " = " + DoubleValue;
                case ParameterType.String: return Name + " = \"" + StringValue + "\"";
                default: return Name + " (not set)";
            }
        }
    }

    /// <summary>
    /// Makes a node's settings visible and changeable through <c>ros2 param</c>.
    /// </summary>
    /// <remarks>
    /// <para>Parameters are how ROS exposes the knobs on a node: sea state,
    /// publish rates, which sensors are fitted. With this attached you get</para>
    ///
    /// <code>
    /// ros2 param list /seanav
    /// ros2 param get  /seanav sea_state.significant_wave_height
    /// ros2 param set  /seanav sea_state.significant_wave_height 2.5
    /// </code>
    ///
    /// <para><b>Parameters are not a protocol of their own.</b> They are six
    /// ordinary services on the node, with names ROS agrees on by convention -
    /// <c>&lt;node&gt;/get_parameters</c> and so on - plus a topic announcing
    /// changes. rclcpp and rclpy build exactly this; it lives in the client
    /// library rather than in rcl, which is why it has to be built here too
    /// rather than switched on.</para>
    ///
    /// <para><b>Call <see cref="Spin"/> regularly.</b> Nothing here runs on its
    /// own. In a simulator that means once per step, which also means a parameter
    /// change lands at a step boundary rather than halfway through one.</para>
    ///
    /// <para><b>Changes are validated before they are applied.</b> A set request
    /// that would change a parameter's type, or touch a read-only one, is refused
    /// with a reason the caller sees - which is what <c>ros2 param set</c> prints.
    /// Accepting it and coercing quietly would be worse.</para>
    /// </remarks>
    public sealed class Ros2ParameterServer : IDisposable
    {
        private readonly Ros2Node _node;
        private readonly Dictionary<string, Parameter> _parameters =
            new Dictionary<string, Parameter>(StringComparer.Ordinal);

        private readonly List<Ros2Service> _services = new List<Ros2Service>();
        private Ros2Publisher _events;
        private bool _disposed;

        /// <summary>Raised after a parameter changes, with the parameter that changed.</summary>
        public event Action<Parameter> Changed;

        /// <summary>Everything currently declared.</summary>
        public IEnumerable<Parameter> All => _parameters.Values;

        /// <summary>How many set requests have been accepted.</summary>
        public long Applied { get; private set; }

        /// <summary>
        /// Serialiser hooks. The parameter services speak <c>rcl_interfaces</c>
        /// types, which live in SEANAV rather than in this library - so the
        /// caller supplies the two functions that turn those messages into bytes
        /// and back.
        /// </summary>
        /// <remarks>
        /// Slightly awkward, and the alternative was worse: making this library
        /// depend on a particular set of generated message classes would undo the
        /// separation that keeps it usable outside SEANAV. See
        /// <c>Ros2ParameterCodec</c> in the SEANAV side for the implementation.
        /// </remarks>
        public interface ICodec
        {
            /// <summary>Decode a get_parameters request into the names it asks for.</summary>
            List<string> ReadNames(byte[] request);

            /// <summary>Encode a get_parameters reply from the values found.</summary>
            byte[] WriteValues(List<Parameter> values);

            /// <summary>Encode a get_parameter_types reply.</summary>
            byte[] WriteTypes(List<Parameter> values);

            /// <summary>Decode a set_parameters request.</summary>
            List<Parameter> ReadParameters(byte[] request);

            /// <summary>Encode a set_parameters reply: one result per parameter.</summary>
            byte[] WriteResults(List<KeyValuePair<bool, string>> results);

            /// <summary>Encode a set_parameters_atomically reply: a single result.</summary>
            byte[] WriteAtomicResult(bool success, string reason);

            /// <summary>Encode a list_parameters reply.</summary>
            byte[] WriteList(List<string> names);

            /// <summary>Encode a describe_parameters reply.</summary>
            byte[] WriteDescriptions(List<Parameter> values);
        }

        private readonly ICodec _codec;

        /// <summary>Offer the parameter services on a node.</summary>
        public Ros2ParameterServer(Ros2Node node, ICodec codec)
        {
            _node = node ?? throw new ArgumentNullException(nameof(node));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));

            // The names are a convention, not something rcl enforces: ros2 param
            // looks for exactly these under the node's own namespace.
            string prefix = "/" + node.Name + "/";

            Offer(prefix + "get_parameters", "rcl_interfaces/srv/GetParameters", HandleGet);
            Offer(prefix + "get_parameter_types", "rcl_interfaces/srv/GetParameterTypes", HandleGetTypes);
            Offer(prefix + "set_parameters", "rcl_interfaces/srv/SetParameters", HandleSet);
            Offer(prefix + "set_parameters_atomically",
                  "rcl_interfaces/srv/SetParametersAtomically", HandleSetAtomically);
            Offer(prefix + "list_parameters", "rcl_interfaces/srv/ListParameters", HandleList);
            Offer(prefix + "describe_parameters",
                  "rcl_interfaces/srv/DescribeParameters", HandleDescribe);
        }

        private readonly List<Func<byte[], byte[]>> _handlers = new List<Func<byte[], byte[]>>();

        private void Offer(string name, string type, Func<byte[], byte[]> handler)
        {
            _services.Add(_node.CreateService(type, name));
            _handlers.Add(handler);
        }

        /// <summary>Declare a parameter, or return the one already declared.</summary>
        /// <remarks>
        /// Declaring twice is not an error - it returns what is already there.
        /// A node that re-declares on reset should keep the value someone set,
        /// not stamp on it.
        /// </remarks>
        public Parameter Declare(Parameter parameter)
        {
            if (parameter == null) throw new ArgumentNullException(nameof(parameter));

            Parameter existing;
            if (_parameters.TryGetValue(parameter.Name, out existing))
                return existing;

            _parameters[parameter.Name] = parameter;
            return parameter;
        }

        /// <summary>Look one up. Null if it was never declared.</summary>
        public Parameter Get(string name)
        {
            Parameter found;
            return _parameters.TryGetValue(name, out found) ? found : null;
        }

        /// <summary>
        /// Answer whatever has arrived. Call this once per step.
        /// </summary>
        public void Spin()
        {
            if (_disposed) return;

            for (int i = 0; i < _services.Count; i++)
            {
                byte[] request;
                while (_services[i].TryTakeRequest(out request))
                    _services[i].Respond(_handlers[i](request));
            }
        }

        // --- the six handlers ------------------------------------------------

        private byte[] HandleGet(byte[] request)
        {
            var found = new List<Parameter>();
            foreach (string name in _codec.ReadNames(request))
            {
                Parameter p = Get(name);

                // ROS answers an unknown parameter with a NOT_SET value rather
                // than an error, so a caller asking for five and getting four
                // still learns which one was missing.
                found.Add(p ?? NotSet(name));
            }
            return _codec.WriteValues(found);
        }

        private byte[] HandleGetTypes(byte[] request)
        {
            var found = new List<Parameter>();
            foreach (string name in _codec.ReadNames(request))
                found.Add(Get(name) ?? NotSet(name));
            return _codec.WriteTypes(found);
        }

        private byte[] HandleSet(byte[] request)
        {
            var results = new List<KeyValuePair<bool, string>>();

            foreach (Parameter requested in _codec.ReadParameters(request))
            {
                string reason;
                bool ok = TryApply(requested, out reason);
                results.Add(new KeyValuePair<bool, string>(ok, reason));
            }
            return _codec.WriteResults(results);
        }

        private byte[] HandleSetAtomically(byte[] request)
        {
            List<Parameter> requested = _codec.ReadParameters(request);

            // Atomically means all or nothing, so check everything before
            // changing anything. Applying half a set and reporting failure would
            // leave the node in a state nobody asked for.
            foreach (Parameter p in requested)
            {
                string why;
                if (!CanApply(p, out why))
                    return _codec.WriteAtomicResult(false, why);
            }

            foreach (Parameter p in requested)
            {
                string ignored;
                TryApply(p, out ignored);
            }
            return _codec.WriteAtomicResult(true, string.Empty);
        }

        private byte[] HandleList(byte[] request)
        {
            var names = new List<string>(_parameters.Keys);
            names.Sort(StringComparer.Ordinal);
            return _codec.WriteList(names);
        }

        private byte[] HandleDescribe(byte[] request)
        {
            var found = new List<Parameter>();
            foreach (string name in _codec.ReadNames(request))
                found.Add(Get(name) ?? NotSet(name));
            return _codec.WriteDescriptions(found);
        }

        // --- validation ------------------------------------------------------

        private bool CanApply(Parameter requested, out string reason)
        {
            Parameter existing = Get(requested.Name);

            if (existing == null)
            {
                reason = "parameter '" + requested.Name + "' has not been declared";
                return false;
            }

            if (existing.ReadOnly)
            {
                reason = "parameter '" + requested.Name + "' is read-only";
                return false;
            }

            if (existing.Type != requested.Type)
            {
                reason = "parameter '" + requested.Name + "' is " + existing.Type +
                         " and cannot become " + requested.Type;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private bool TryApply(Parameter requested, out string reason)
        {
            if (!CanApply(requested, out reason)) return false;

            Parameter target = Get(requested.Name);
            switch (requested.Type)
            {
                case ParameterType.Bool: target.Set(requested.BoolValue); break;
                case ParameterType.Integer: target.Set(requested.IntegerValue); break;
                case ParameterType.Double: target.Set(requested.DoubleValue); break;
                case ParameterType.String: target.Set(requested.StringValue); break;
                default:
                    reason = "cannot set a parameter of type " + requested.Type;
                    return false;
            }

            Applied++;

            Action<Parameter> handler = Changed;
            if (handler != null) handler(target);

            return true;
        }

        private static Parameter NotSet(string name)
        {
            Parameter p = Parameter.NotSetNamed(name);
            p.Description = "not declared";
            return p;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (Ros2Service s in _services) s.Dispose();
            _services.Clear();

            if (_events != null) { _events.Dispose(); _events = null; }
        }
    }
}
