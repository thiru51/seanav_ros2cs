// Serves example_interfaces/action/Fibonacci - the action everyone tests with.
//
//     ros2 action list
//     ros2 action send_goal /fibonacci example_interfaces/action/Fibonacci \
//         "{order: 8}" --feedback
//
// Ctrl+C during a goal sends a cancel, which the server honours.

using System;
using System.Collections.Generic;
using System.Threading;
using SeaNav.Core.Ros;
using SeaNav.Ros2;
using SeaNav.Ros2.Unity;
using example_interfaces.action;

internal static class Program
{
    private static void Main(string[] args)
    {
        double seconds = args.Length > 0 ? double.Parse(args[0]) : 60.0;

        var codec = new Ros2ActionCodec<
            Fibonacci_SendGoal_Request, Fibonacci_SendGoal_Response,
            Fibonacci_GetResult_Request, Fibonacci_GetResult_Response,
            Fibonacci_FeedbackMessage>();

        using (Ros2Context ros = new Ros2Context())
        using (Ros2Node node = ros.CreateNode("seanav_action_demo"))
        using (var server = new Ros2ActionServer(
                   node, "example_interfaces/action/Fibonacci", "/fibonacci", codec))
        {
            var sequence = new List<int>();
            ActionGoal running = null;
            int order = 0;

            server.OnGoal = goal =>
            {
                var request = new Fibonacci_Goal();
                RosCdr.DeserialiseAllowingSlack(goal.GoalCdr, request);

                // Rejecting is a normal answer. One at a time is a real
                // constraint, and saying so beats queueing silently.
                if (running != null && running.Active)
                {
                    Console.WriteLine("rejected: already running one");
                    return false;
                }

                order = request.Order;
                sequence.Clear();
                sequence.Add(0);
                sequence.Add(1);
                running = goal;

                Console.WriteLine("accepted goal, order " + order);
                return true;
            };

            server.OnCancel = goal => Console.WriteLine("cancel asked for " + goal);

            double t = 0;
            DateTime giveUp = DateTime.UtcNow.AddSeconds(seconds);

            while (DateTime.UtcNow < giveUp && ros.Ok)
            {
                server.Spin(t);

                if (running != null && running.Active)
                {
                    if (running.CancelRequested)
                    {
                        Console.WriteLine("cancelled at " + sequence.Count + " terms");
                        server.Finish(running, GoalStatus.Canceled, Result(sequence), t);
                        running = null;
                    }
                    else if (sequence.Count < order + 2)
                    {
                        sequence.Add(sequence[sequence.Count - 1] +
                                     sequence[sequence.Count - 2]);

                        var fb = new Fibonacci_Feedback();
                        fb.Sequence.AddRange(sequence);
                        server.PublishFeedback(running, RosCdr.Serialise(fb));

                        Thread.Sleep(400);   // slow on purpose, so feedback is visible
                    }
                    else
                    {
                        Console.WriteLine("done: " + string.Join(", ", sequence));
                        server.Finish(running, GoalStatus.Succeeded, Result(sequence), t);
                        running = null;
                    }
                }

                t += 0.01;
                Thread.Sleep(5);
            }
        }
    }

    private static byte[] Result(List<int> sequence)
    {
        var result = new Fibonacci_Result();
        result.Sequence.AddRange(sequence);
        return RosCdr.Serialise(result);
    }
}
