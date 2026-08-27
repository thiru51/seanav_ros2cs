using System;
using System.Collections.Generic;

namespace SeaNav.Ros2
{
    /// <summary>
    /// Where a goal has got to. Values are the wire numbers from
    /// <c>action_msgs/msg/GoalStatus</c>.
    /// </summary>
    public enum GoalStatus : sbyte
    {
        Unknown = 0,
        Accepted = 1,
        Executing = 2,
        Canceling = 3,
        Succeeded = 4,
        Canceled = 5,
        Aborted = 6
    }

    /// <summary>One goal a client has asked for, and how it is going.</summary>
    public sealed class ActionGoal
    {
        /// <summary>The 16-byte id the client chose. Feedback and results quote it back.</summary>
        public byte[] Id { get; }

        /// <summary>The goal message, still as CDR bytes for the caller to decode.</summary>
        public byte[] GoalCdr { get; }

        /// <summary>Where it has got to.</summary>
        public GoalStatus Status { get; internal set; }

        /// <summary>True once a client has asked for this goal to stop.</summary>
        public bool CancelRequested { get; internal set; }

        /// <summary>The result, once there is one.</summary>
        public byte[] ResultCdr { get; internal set; }

        internal ActionGoal(byte[] id, byte[] goalCdr)
        {
            Id = id;
            GoalCdr = goalCdr;
            Status = GoalStatus.Accepted;
        }

        /// <summary>True while this goal is still worth working on.</summary>
        public bool Active =>
            Status == GoalStatus.Accepted ||
            Status == GoalStatus.Executing ||
            Status == GoalStatus.Canceling;

        public override string ToString() =>
            "goal " + BitConverter.ToString(Id, 0, 4).Replace("-", "") + " " + Status;
    }

    /// <summary>
    /// Serves a ROS 2 action: a long-running job a client can watch and cancel.
    /// </summary>
    /// <remarks>
    /// <para><b>An action is not a thing on the wire.</b> It is three services
    /// and two topics with names ROS agrees on, plus a state machine deciding
    /// what a goal is allowed to do next. rclcpp and rclpy build exactly this;
    /// rcl does not, which is why it is built here.</para>
    ///
    /// <code>
    /// &lt;action&gt;/_action/send_goal      service
    /// &lt;action&gt;/_action/cancel_goal    service
    /// &lt;action&gt;/_action/get_result     service
    /// &lt;action&gt;/_action/feedback       topic
    /// &lt;action&gt;/_action/status         topic
    /// </code>
    ///
    /// <para><b>Why a simulator wants them.</b> "Run this scenario to completion"
    /// is exactly the shape an action fits: it takes minutes, you want progress
    /// while it runs, and you want to be able to stop it. A service call would
    /// block; a topic could not tell you when it finished.</para>
    ///
    /// <para><b>This class does not run your job.</b> It accepts goals, tracks
    /// their status, carries feedback and hands back results — you do the work
    /// and tell it what happened. That keeps the work on your thread and at your
    /// step boundaries, which for a simulator is the only sane arrangement.</para>
    ///
    /// <para><b>Feedback and result encoding is the caller's.</b> The types come
    /// from a <c>.action</c> file and live wherever the generated classes live,
    /// so this class deals in bytes and lets the caller wrap them. The codec
    /// interface is the same arrangement the parameter server uses and for the
    /// same reason.</para>
    /// </remarks>
    public sealed class Ros2ActionServer : IDisposable
    {
        /// <summary>
        /// Encoding hooks for the six derived types an action carries.
        /// </summary>
        /// <remarks>
        /// Implemented on the SEANAV side, where the generated classes are. See
        /// <c>Ros2ActionCodec</c>.
        /// </remarks>
        public interface ICodec
        {
            /// <summary>Pull the goal id and the goal itself out of a send_goal request.</summary>
            void ReadSendGoal(byte[] request, out byte[] goalId, out byte[] goalCdr);

            /// <summary>Build a send_goal reply.</summary>
            byte[] WriteSendGoalResponse(bool accepted, double stampSeconds);

            /// <summary>Pull the goal id out of a get_result request.</summary>
            byte[] ReadGetResultGoalId(byte[] request);

            /// <summary>Build a get_result reply from a status and a result payload.</summary>
            byte[] WriteGetResultResponse(sbyte status, byte[] resultCdr);

            /// <summary>Wrap a feedback payload with the goal it belongs to.</summary>
            byte[] WriteFeedbackMessage(byte[] goalId, byte[] feedbackCdr);

            /// <summary>Build a status array covering every goal we know about.</summary>
            byte[] WriteStatusArray(IEnumerable<ActionGoal> goals, double stampSeconds);

            /// <summary>Pull the goal id out of a cancel request. Zeroed id means "all".</summary>
            byte[] ReadCancelGoalId(byte[] request);

            /// <summary>Build a cancel reply listing what was accepted for cancellation.</summary>
            byte[] WriteCancelResponse(List<ActionGoal> cancelling, double stampSeconds);
        }

        private readonly Ros2Node _node;
        private readonly ICodec _codec;

        private readonly Ros2Service _sendGoal;
        private readonly Ros2Service _cancelGoal;
        private readonly Ros2Service _getResult;
        private readonly Ros2Publisher _feedback;
        private readonly Ros2Publisher _status;

        private readonly Dictionary<string, ActionGoal> _goals =
            new Dictionary<string, ActionGoal>(StringComparer.Ordinal);

        // Result requests that arrived before the goal finished. ROS clients ask
        // for the result as soon as the goal is accepted and wait, so most
        // requests land here first.
        private readonly List<KeyValuePair<string, Ros2Service>> _pendingResults =
            new List<KeyValuePair<string, Ros2Service>>();

        private bool _disposed;

        /// <summary>The action name, e.g. <c>/run_scenario</c>.</summary>
        public string ActionName { get; }

        /// <summary>
        /// Called when a goal arrives. Return false to reject it.
        /// </summary>
        /// <remarks>
        /// Rejecting is a normal answer, not a failure — "I am already running
        /// one" is a perfectly good reason, and a client is told immediately
        /// rather than left waiting.
        /// </remarks>
        public Func<ActionGoal, bool> OnGoal;

        /// <summary>Called when a client asks for a goal to stop.</summary>
        public Action<ActionGoal> OnCancel;

        /// <summary>Goals we know about, finished ones included.</summary>
        public IEnumerable<ActionGoal> Goals => _goals.Values;

        /// <summary>The goal currently being worked on, or null.</summary>
        public ActionGoal Active
        {
            get
            {
                foreach (ActionGoal g in _goals.Values)
                    if (g.Active) return g;
                return null;
            }
        }

        /// <summary>
        /// Offer an action.
        /// </summary>
        /// <param name="node">The node to offer it on.</param>
        /// <param name="actionType">e.g. <c>example_interfaces/action/Fibonacci</c>.</param>
        /// <param name="actionName">e.g. <c>/fibonacci</c>.</param>
        /// <param name="codec">Encoding for the derived types.</param>
        public Ros2ActionServer(Ros2Node node, string actionType, string actionName, ICodec codec)
        {
            _node = node ?? throw new ArgumentNullException(nameof(node));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            ActionName = actionName;

            // The _action infix and these five names are the convention every
            // ROS action client looks for. They are not negotiable.
            string prefix = actionName + "/_action/";

            _sendGoal = node.CreateService(actionType + "_SendGoal", prefix + "send_goal");
            _cancelGoal = node.CreateService("action_msgs/srv/CancelGoal", prefix + "cancel_goal");
            _getResult = node.CreateService(actionType + "_GetResult", prefix + "get_result");

            _feedback = node.CreatePublisher(
                actionType + "_FeedbackMessage", prefix + "feedback", QosProfile.Default);

            // Status is latched: a client that connects mid-goal still needs to
            // learn what is going on, and it will not get another update until
            // something changes.
            _status = node.CreatePublisher(
                "action_msgs/msg/GoalStatusArray", prefix + "status", QosProfile.Latched);
        }

        /// <summary>
        /// Answer whatever has arrived. Call once per step.
        /// </summary>
        /// <param name="nowSeconds">Current time, for the stamps in the replies.</param>
        public void Spin(double nowSeconds)
        {
            if (_disposed) return;

            HandleSendGoal(nowSeconds);
            HandleCancel(nowSeconds);
            HandleGetResult();
            AnswerFinishedGoals();
        }

        private void HandleSendGoal(double now)
        {
            byte[] request;
            while (_sendGoal.TryTakeRequest(out request))
            {
                byte[] goalId, goalCdr;
                _codec.ReadSendGoal(request, out goalId, out goalCdr);

                var goal = new ActionGoal(goalId, goalCdr);

                bool accepted = OnGoal == null || OnGoal(goal);
                if (accepted)
                {
                    goal.Status = GoalStatus.Executing;
                    _goals[Key(goalId)] = goal;
                }

                _sendGoal.Respond(_codec.WriteSendGoalResponse(accepted, now));
                if (accepted) PublishStatus(now);
            }
        }

        private void HandleCancel(double now)
        {
            byte[] request;
            while (_cancelGoal.TryTakeRequest(out request))
            {
                byte[] id = _codec.ReadCancelGoalId(request);
                var cancelling = new List<ActionGoal>();

                // An all-zero id means "everything you have running", which is
                // what `ros2 action send_goal` sends on Ctrl+C.
                bool all = IsZero(id);

                foreach (ActionGoal g in _goals.Values)
                {
                    if (!g.Active) continue;
                    if (!all && Key(g.Id) != Key(id)) continue;

                    g.CancelRequested = true;
                    g.Status = GoalStatus.Canceling;
                    cancelling.Add(g);

                    if (OnCancel != null) OnCancel(g);
                }

                _cancelGoal.Respond(_codec.WriteCancelResponse(cancelling, now));
                PublishStatus(now);
            }
        }

        private void HandleGetResult()
        {
            byte[] request;
            while (_getResult.TryTakeRequest(out request))
            {
                byte[] id = _codec.ReadGetResultGoalId(request);
                string key = Key(id);

                ActionGoal goal;
                if (_goals.TryGetValue(key, out goal) && !goal.Active)
                {
                    _getResult.Respond(
                        _codec.WriteGetResultResponse((sbyte)goal.Status, goal.ResultCdr));
                    continue;
                }

                // Still running - or unknown. Hold the request until it finishes.
                //
                // This is the one place the service abstraction leaks: a service
                // normally answers straight away, and an action deliberately does
                // not. Ros2Service addresses a reply to whoever last called, so
                // only one can be outstanding, which is why this is recorded and
                // answered in order.
                _pendingResults.Add(new KeyValuePair<string, Ros2Service>(key, _getResult));
            }
        }

        private void AnswerFinishedGoals()
        {
            for (int i = _pendingResults.Count - 1; i >= 0; i--)
            {
                ActionGoal goal;
                if (!_goals.TryGetValue(_pendingResults[i].Key, out goal)) continue;
                if (goal.Active) continue;

                _pendingResults[i].Value.Respond(
                    _codec.WriteGetResultResponse((sbyte)goal.Status, goal.ResultCdr));
                _pendingResults.RemoveAt(i);
            }
        }

        /// <summary>Send progress for a goal that is still running.</summary>
        public void PublishFeedback(ActionGoal goal, byte[] feedbackCdr)
        {
            if (_disposed || goal == null) return;
            _feedback.Publish(_codec.WriteFeedbackMessage(goal.Id, feedbackCdr));
        }

        /// <summary>Finish a goal, successfully or otherwise.</summary>
        /// <remarks>
        /// The status matters to the client: <c>Succeeded</c>, <c>Canceled</c>
        /// and <c>Aborted</c> mean different things and `ros2 action send_goal`
        /// prints which one it got. A job that stopped because it was asked to
        /// should not report success.
        /// </remarks>
        public void Finish(ActionGoal goal, GoalStatus status, byte[] resultCdr, double nowSeconds)
        {
            if (_disposed || goal == null) return;

            goal.Status = status;
            goal.ResultCdr = resultCdr ?? new byte[] { 0, 1, 0, 0 };

            PublishStatus(nowSeconds);
            AnswerFinishedGoals();
        }

        private void PublishStatus(double now)
        {
            _status.Publish(_codec.WriteStatusArray(_goals.Values, now));
        }

        private static string Key(byte[] id) => BitConverter.ToString(id);

        private static bool IsZero(byte[] id)
        {
            if (id == null) return true;
            for (int i = 0; i < id.Length; i++) if (id[i] != 0) return false;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _sendGoal.Dispose();
            _cancelGoal.Dispose();
            _getResult.Dispose();
            _feedback.Dispose();
            _status.Dispose();
        }
    }
}
