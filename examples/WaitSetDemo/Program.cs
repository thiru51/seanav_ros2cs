using System;
using System.Diagnostics;
using SeaNav.Ros2;

// Shows that a wait set really does sleep, and really does wake.
//
// Two things are worth proving and neither is obvious from the API:
//   1. A timeout costs no CPU. Polling for a second burns a core; this does not.
//   2. A message wakes it promptly, rather than at the end of the timeout.
//
// Run this, then in another terminal:
//   ros2 topic pub -1 /chatter std_msgs/msg/String "{data: hello}"
class Program
{
    static int Main(string[] args)
    {
        double seconds = args.Length > 0 ? double.Parse(args[0]) : 10.0;
        bool poll = Array.IndexOf(args, "--poll") >= 0;

        using (var context = new Ros2Context())
        using (var node = context.CreateNode("waitset_demo"))
        using (var sub = node.CreateSubscription("std_msgs/msg/String", "/chatter"))
        using (var wait = new Ros2WaitSet(context))
        {
            wait.Add(sub);
            Console.WriteLine("READY listening on /chatter for " + seconds + "s" +
                              (poll ? " [polling, for comparison]" : ""));

            var total = Stopwatch.StartNew();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            int messages = 0;
            int unused = 0;

            while (total.Elapsed.TotalSeconds < seconds)
            {
                var slept = Stopwatch.StartNew();

                bool ready;
                if (poll)
                {
                    // What this library did before wait sets existed, and what a
                    // simulator still does: ask, repeatedly, as fast as you can.
                    // Kept here so the cost of the alternative is a measurement
                    // rather than a claim.
                    ready = false;
                    while (slept.Elapsed.TotalSeconds < 1.0)
                    {
                        byte[] got;
                        if (sub.TryTake(out got))
                        {
                            messages++;
                            Console.WriteLine(string.Format(
                                "WOKE after {0:F3}s, {1} bytes", slept.Elapsed.TotalSeconds,
                                got.Length));
                            ready = false;   // already handled it here
                            break;
                        }
                    }
                }
                else
                {
                    ready = wait.Wait(1.0);
                }

                slept.Stop();

                if (!ready)
                {
                    if (!poll)
                        Console.WriteLine(string.Format("TIMEOUT after {0:F3}s (expected ~1.000)",
                                                        slept.Elapsed.TotalSeconds));
                    else unused++;
                    continue;
                }

                if (wait.IsReady(sub))
                {
                    byte[] payload;
                    while (sub.TryTake(out payload))
                    {
                        messages++;
                        Console.WriteLine(string.Format(
                            "WOKE after {0:F3}s, {1} bytes", slept.Elapsed.TotalSeconds,
                            payload.Length));
                    }
                }
            }

            var used = Process.GetCurrentProcess().TotalProcessorTime - cpu;
            Console.WriteLine(string.Format(
                "SUMMARY wall={0:F1}s cpu={1:F3}s ({2:F2}% of one core) messages={3} " +
                "wakeups={4} timeouts={5}",
                total.Elapsed.TotalSeconds, used.TotalSeconds,
                100.0 * used.TotalSeconds / total.Elapsed.TotalSeconds,
                messages, wait.Wakeups, wait.Timeouts));
        }
        return 0;
    }
}
