// Offers /add_two_ints, the service everyone tests ROS with.
//
// Run it, then from another terminal:
//
//     ros2 service list
//     ros2 service call /add_two_ints example_interfaces/srv/AddTwoInts "{a: 41, b: 1}"
//
// If that returns 42, then a stock ROS client talked to us, our CDR decoder
// read its request, and our encoder produced a reply it understood.

using System;
using System.Threading;
using SeaNav.Core.Ros;
using SeaNav.Ros2;

internal static class Program
{
    private static void Main(string[] args)
    {
        double seconds = args.Length > 0 ? double.Parse(args[0]) : 60.0;

        using (Ros2Context ros = new Ros2Context())
        using (Ros2Node node = ros.CreateNode("seanav_service_demo"))
        {
            Ros2Service service = node.CreateService(
                "example_interfaces/srv/AddTwoInts", "/add_two_ints");

            Console.WriteLine("Offering /add_two_ints. Try:");
            Console.WriteLine("  ros2 service call /add_two_ints " +
                              "example_interfaces/srv/AddTwoInts \"{a: 41, b: 1}\"");
            Console.WriteLine();

            var request = new example_interfaces.srv.AddTwoInts_Request();
            var response = new example_interfaces.srv.AddTwoInts_Response();

            DateTime giveUp = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < giveUp && ros.Ok)
            {
                byte[] bytes;
                if (!service.TryTakeRequest(out bytes))
                {
                    Thread.Sleep(5);
                    continue;
                }

                RosCdr.Deserialise(bytes, request);
                response.Sum = request.A + request.B;

                Console.WriteLine($"{request.A} + {request.B} = {response.Sum}");
                service.Respond(RosCdr.Serialise(response));
            }

            Console.WriteLine("Answered " + service.Answered + " request(s).");
        }
    }
}
