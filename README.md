# SeaNav.Ros2

A ROS 2 binding for C#. No code generator, no generated native libraries, no colcon.

It makes real ROS 2 nodes through `rcl`, so `ros2 node list` finds them, `ros2 topic echo` reads
them, and QoS settings work the way you'd expect. What you get is a single managed DLL and nothing
to compile natively.

Written for [SEANAV](https://github.com/thiru51/SEANAV_1), but there's nothing ship-specific in it.

```csharp
using (var ros = new Ros2Context())
using (var node = ros.CreateNode("seanav_talker"))
{
    var pub = node.CreatePublisher("sensor_msgs/msg/Imu", "/seanav/imu", QosProfile.SensorData);

    var imu = new RosImu();
    imu.Header.FrameId = "seanav_imu";
    pub.Publish(RosCdr.Serialise(imu));
}
```

## The idea

If you want to talk to ROS 2 from C#, the usual answer is
[RobotecAI's ros2cs](https://github.com/RobotecAI/ros2cs). It's good, and this project was measured
against it before anything was written. It works by generating, for every message type, a C# class
and a small C library, then copying your data field by field into a C struct which DDS walks again
to produce the bytes that actually go on the wire.

On a stock Jazzy install that comes to **1,478 shared libraries and 59 MB**, and a colcon build
every time a message package changes.

There's another way in. `rcl_publish_serialized_message` takes the wire bytes directly, and every
message package already exports its type description under a name you can work out from the message
name:

```
rosidl_typesupport_c__get_message_type_support_handle__sensor_msgs__msg__Imu
```

So: one symbol lookup per message type, once, when you create the publisher. Then hand over bytes.
That's the whole trick, and it's why there's no generator here.

**It is not faster, and it would be silly to claim otherwise.** Measured on Jazzy with Fast DDS,
RobotecAI's field-by-field copying takes 0.353 µs out of a 1.095 µs IMU publish. Their design is
not slow. The reasons to prefer this one are that it's far smaller, it builds with plain `dotnet`,
and it runs anywhere .NET runs — including outside a game engine, which matters if your simulator
has a headless mode.

## What works and what doesn't

| | |
|---|---|
| Publishing, any message type, any QoS | works |
| Real nodes visible to `ros2 node list` and `ros2 topic info` | works |
| Subscribing | works — `Ros2Subscription.TryTake` |
| Services and clients | **not possible this way** |

The services one isn't a to-do item, it's a wall. `rcl` gives you `rcl_send_request` and
`rcl_take_request`, and there is no "as raw bytes" version of either. A service call needs the real
C struct, which is exactly the thing this design avoids having. If you need services today, use
ros2cs.

## What you need

- ROS 2 **Jazzy**, Linux, x86-64. Other distributions will probably work; nobody has checked.
- .NET SDK 8 to build. The library itself targets `netstandard2.1`, so Unity and .NET load the
  same DLL.
- No colcon, no ROS workspace, no C compiler.

## Building and running

```bash
./tools/build.sh /path/to/SEANAV
dotnet examples/Talker/bin/Release/net8.0/Talker.dll
```

Then, in another terminal with ROS sourced:

```bash
ros2 topic echo /seanav/imu sensor_msgs/msg/Imu
```

## Testing it

`tools/interop_test.sh` checks both directions against ROS's own tools, which is the only test
that really means anything here — talking to yourself proves nothing.

```bash
source /opt/ros/jazzy/setup.bash
./tools/interop_test.sh /path/to/SEANAV
```

Nine checks: our publisher into `ros2 topic echo`, `ros2 topic pub` into our subscriber, and
`ros2 node list` / `ros2 topic info` seeing us as an ordinary node. It exits 3 and says so if no
ROS is sourced, rather than passing quietly.

The CDR encoder itself has its own 76 checks in SEANAV (`./tools/verify.sh Ros`), including the
worked byte example from the OMG specification. Those need no ROS at all.

## Where the CDR encoder lives

The code that turns a message into bytes lives in SEANAV, under
`unity/Assets/SEANAV/Core/Runtime/Ros/`, and this project compiles those files in by path (see the
`SeaNavCore` property in the .csproj). One copy, one place to fix bugs, and it stays inside the test
suite that checks it.

The downside is that you currently need a SEANAV checkout to build this. The alternative would be
for this repo to own the encoder and for SEANAV to copy it in during setup. Worth deciding on
purpose rather than drifting into.

## Things that will bite you if you port this

**The message type lookup.** That predictable function name is a convention of ROS's code
generator, not a promise. It has held for every ROS 2 release so far. `RclInterop.MessageTypeSupport`
is the only place that would need changing.

**The struct layouts** in `Native/RclInterop.cs` were measured, not guessed — a small C program
compiled against Jazzy's own headers printed `sizeof` and `offsetof` for each one. A wrong offset
doesn't give you a wrong answer, it gives you a crash or silent memory corruption, so re-measure
before supporting another distribution.

Two other things learned the hard way, both explained in the source:

- **Libraries are opened by explicit path, not `[DllImport]`.** Linux reads `LD_LIBRARY_PATH` when
  the process starts, so a game can't fix its own library path afterwards.
- **Small interop structs must declare their fields**, never just `Size = 8`. On 64-bit Linux a
  struct of 16 bytes or less comes back from a C function in registers, and which registers depends
  on the field types. An empty struct doesn't say, so .NET returns it through memory instead and
  you get garbage. That one cost an afternoon: the layout was right and only the calling convention
  was wrong, which no error code will ever tell you.

## Licence

Apache 2.0.

No code from `ros2cs` or `ros2-for-unity` is used here. This talks to the public ROS 2 C API, which
is the same API they talk to. Where their design taught us something, the source says so.
