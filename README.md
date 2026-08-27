# SeaNav.Ros2

**ROS 2 from C#. No code generator, no generated native libraries, no colcon.**

It creates real ROS 2 nodes through `rcl`, so `ros2 node list` finds them, `ros2 topic echo` reads
them, `ros2 service call` calls them, and QoS behaves the way you'd expect. What you get is one
managed DLL and nothing to compile natively.

Written for [SEANAV](https://github.com/thiru51/SEANAV_1), but there's nothing ship-specific in it.

```csharp
using (var ros  = new Ros2Context())
using (var node = ros.CreateNode("my_node"))
{
    var pub = node.CreatePublisher("sensor_msgs/msg/Imu", "/imu", QosProfile.SensorData);
    var sub = node.CreateSubscription("geometry_msgs/msg/Twist", "/cmd_vel");
    var srv = node.CreateService("example_interfaces/srv/AddTwoInts", "/add_two_ints");

    pub.Publish(RosCdr.Serialise(myImu));
}
```

---

## Contents

- [What works](#what-works)
- [The idea](#the-idea)
- [Requirements](#requirements)
- [Building](#building)
- [**Sourcing ROS — read this if nothing works**](#sourcing-ros--read-this-if-nothing-works)
- [Publishing](#publishing)
- [Subscribing](#subscribing)
- [Services and clients](#services-and-clients)
- [Parameters](#parameters)
- [Actions](#actions)
- [Quality of Service](#quality-of-service)
- [Message types](#message-types)
- [Testing](#testing)
- [How it works inside](#how-it-works-inside)
- [Porting to another distribution](#porting-to-another-distribution)
- [Known limits](#known-limits)

---

## What works

| | |
|---|---|
| Publishing, any message type, any QoS | yes |
| Subscribing | yes |
| **Services and clients** | **yes** |
| Real nodes in `ros2 node list` / `topic info` / `service list` | yes |
| Custom message and service types | yes — see [Message types](#message-types) |
| **Parameters** | **yes** — `ros2 param list/get/set/describe` |
| **Actions** | **yes** — goals, feedback, cancel, result |
| Wait sets (blocking instead of polling) | no |

Every "yes" above was checked by running the ordinary ROS command-line tools against a node from
this library. Not by a test that calls our own code — by the same programs a ROS user already has.
The exact commands are in [Testing](#testing).

---

## The idea

The established C# route to ROS 2 is [RobotecAI's ros2cs](https://github.com/RobotecAI/ros2cs).
It's good work, and this project was measured against it before a line was written. It generates,
for every message type, a C# class **and a small C library**, then copies your data field by field
into a C struct which DDS walks again to produce the bytes that go on the wire.

On a stock Jazzy install that comes to **1,478 shared libraries and 59 MB**, plus a colcon build
whenever a message package changes.

There is another way in. `rcl_publish_serialized_message` takes the wire bytes directly, and every
message package already exports its type description under a name you can work out from the message
name:

```
rosidl_typesupport_c__get_message_type_support_handle__sensor_msgs__msg__Imu
```

So: **one symbol lookup per message type, once**, when you create the publisher. Then hand over
bytes. That's the whole trick, and it's why there's no generator here.

### It is not faster, and it would be silly to claim otherwise

Measured on Jazzy with Fast DDS, RobotecAI's field-by-field copying takes **0.353 µs** of a
**1.095 µs** IMU publish. Their design is not slow. The reasons to prefer this one are that it's a
fraction of the size, it builds with plain `dotnet`, and it runs anywhere .NET runs — including
outside a game engine, which matters if your simulator has a headless mode.

---

## Requirements

- **ROS 2 Jazzy**, Linux, x86-64. Other distributions will probably work and nobody has checked —
  see [Porting](#porting-to-another-distribution).
- **.NET SDK 8** to build. The library targets `netstandard2.1`, so Unity and .NET load the same DLL.
- No colcon, no ROS workspace, no C compiler.

---

## Building

```bash
./tools/build.sh /path/to/SEANAV
```

The path to SEANAV is only for the **examples**, which build actual messages. The library itself
needs nothing — see [Message types](#message-types) for why.

---

## Sourcing ROS — read this if nothing works

**You must start your process from a terminal that has sourced ROS.**

```bash
source /opt/ros/jazzy/setup.bash
```

For Unity, that means launching the editor *from that terminal*, not from the Hub or a desktop
icon. To fix it everywhere, put the line in `~/.profile` and log out and back in.

### Why — because "just source it" is not an explanation

Three facts that combine badly:

**1. ROS 2 is built without an RPATH.** A shared library can record where its own dependencies
live; ROS's don't. `librcl.so` alone needs eleven other libraries — `librmw.so`, `librcutils.so`
and the rest — and nothing in the file says where they are.

**2. So the loader falls back to `LD_LIBRARY_PATH`.** That's the environment variable listing
folders to search. `source setup.bash` is mostly just setting it.

**3. Linux reads `LD_LIBRARY_PATH` once, when the process starts.** Not when you call a function —
when the process starts. So by the time any of your code runs, the search path is already fixed and
**nothing you do can change it.** Setting the variable from inside the program is too late. The
loader made its copy before `main` ran.

That's the whole story. It's not ROS being awkward or Unity being awkward; it's how dynamic linking
works on Linux.

### What this library does to help

Quite a lot, and it still isn't enough on its own:

- Loads the core libraries **by explicit path** from a folder you nominate, so `LD_LIBRARY_PATH`
  isn't needed for those.
- When one of those fails for a missing dependency, reads the name out of `dlerror`, loads *that*
  from the same folder, and retries — recursively.
- Derives `AMENT_PREFIX_PATH` and sets it with a **native** `setenv` (`.NET`'s
  `Environment.SetEnvironmentVariable` only updates .NET's own copy on Unix; C code calling
  `getenv` never sees it).
- Preloads the DDS backend, which `librmw_implementation` otherwise opens by bare name.

Together those take an unsourced process from *"cannot open librcl.so"* to *"cannot find a type
support library"*. **That's as far as it goes.** ROS opens a type support library per message
package as it needs them, and preloading everything is not a way out: of the 364 libraries in a
Jazzy install, one pass loads 32 — the rest depend on each other in an order you'd have to
reconstruct.

So the library folder is a genuine help and not a substitute. Source ROS.

---

## Publishing

```csharp
var pub = node.CreatePublisher("sensor_msgs/msg/Imu", "/imu", QosProfile.SensorData);
pub.Publish(RosCdr.Serialise(imu));
```

`Publish` takes **bytes**, not an object. The bytes are CDR — the format ROS uses on the wire — and
SEANAV's `CdrWriter` produces them. The unmanaged buffer is reused between calls and only grows, so
publishing at 200 Hz doesn't allocate.

---

## Subscribing

```csharp
var sub = node.CreateSubscription("geometry_msgs/msg/Twist", "/cmd_vel");

byte[] bytes;
while (sub.TryTake(out bytes))          // drain everything queued
    RosCdr.Deserialise(bytes, twist);
```

It doesn't call you back — you ask whether anything arrived. That suits a simulator, which already
has a loop running at a fixed rate and wants to read commands once per step. The cost is that a
tight loop with nothing to read spins the CPU; sleep a little, as the Listener example does.

---

## Services and clients

```csharp
// Offering one
var service = node.CreateService("example_interfaces/srv/AddTwoInts", "/add_two_ints");

byte[] request;
if (service.TryTakeRequest(out request))
{
    RosCdr.Deserialise(request, req);
    resp.Sum = req.A + req.B;
    service.Respond(RosCdr.Serialise(resp));
}

// Calling one
var client = node.CreateClient("example_interfaces/srv/AddTwoInts", "/add_two_ints");
byte[] reply = client.CallAndWait(RosCdr.Serialise(req), timeoutSeconds: 5.0);
```

### These took a correction

This README used to say services were impossible here — that `rcl_send_request` wants a native C
struct, has no `_serialized` variant, and that was that. **That was wrong**, and it came from
checking for the `_serialized` variant and stopping there.

The way through needs no generated code:

1. Every message package exports `pkg__srv__Name_Request__create()`, so the native struct can be
   **allocated by name**.
2. `rmw_deserialize` fills it from CDR bytes in **one call**.
3. `rmw_serialize` converts the reply back the same way.

Two conversions per call instead of one, both done by the middleware's own routines — the same ones
a C++ node uses.

Requests are matched to replies **by sequence number**, not arrival order. Send two and the answers
can come back the other way round, which is why `Call` returns the number it used.

Polling rather than callbacks, deliberately: in a simulator "reset the episode" should happen at a
step boundary, and a callback fired from a middleware thread cannot promise that.

---

## Parameters

### What a parameter is, if you have not met one

A ROS node usually has settings you want to change without editing code or restarting: a gain, a
frame name, a sensor rate. ROS calls these **parameters**, and every ROS tool knows how to list,
read and change them on a running node.

Here is what that looks like from a terminal, against a node from this library:

```bash
ros2 param list /seanav
ros2 param get  /seanav vessel.mass
ros2 param set  /seanav vessel.mass 3200.0
ros2 param describe /seanav vessel.mass
```

### Declaring them

```csharp
var parameters = new Ros2ParameterServer(node, codec);

parameters.Declare(Parameter.Double("vessel.mass", 3000.0),
                   description: "Displacement in kilograms");
parameters.Declare(Parameter.String("vessel.name", "KVLCC2"), readOnly: true);

// In your loop, so changes arrive at a step boundary rather than
// from a middleware thread halfway through a physics update:
parameters.SpinOnce();

double mass = parameters.Get("vessel.mass").AsDouble;
```

### The part worth knowing

**A parameter is not a thing on the wire.** There is no parameter protocol. What ROS actually
agrees on is **six ordinary services** with conventional names — `list_parameters`,
`get_parameters`, `set_parameters`, `set_parameters_atomically`, `describe_parameters`,
`get_parameter_types` — plus a topic announcing changes. `rclcpp` and `rclpy` build them; `rcl`
does not, which is why they had to be built here. Once [services](#services-and-clients) worked,
this was mostly wiring.

The care went into the refusals. A `set` that would **change a parameter's type**, or touch one
declared **read-only**, is declined *with a reason*, and that reason travels back through the CLI:

```
$ ros2 param set /seanav vessel.name Titanic
Setting parameter failed: parameter 'vessel.name' is read-only
```

`set_parameters_atomically` validates every parameter in the batch **before changing any of them**,
so a batch that is going to fail changes nothing at all.

---

## Actions

### What an action is, if you have not met one

A **service** is a question with one answer, and you wait for it. That is wrong for anything slow.
"Navigate to this waypoint" might take four minutes, you want progress while it runs, and you want
to be able to give up partway.

That is an **action**: send a **goal**, get told whether it was **accepted**, receive **feedback**
while it runs, **cancel** if you change your mind, and collect a **result** at the end.

```bash
ros2 action list
ros2 action send_goal /fibonacci example_interfaces/action/Fibonacci "{order: 8}" --feedback
```

### Serving one

```csharp
var server = new Ros2ActionServer(node, "example_interfaces/action/Fibonacci",
                                  "/fibonacci", codec);

server.SpinOnce();                      // in your loop

ActionGoal goal;
if (server.TryTakeGoal(out goal))
{
    server.Accept(goal);                // or server.Reject(goal)
}

foreach (var running in server.Active)
{
    if (running.CancelRequested) { server.Cancelled(running); continue; }

    server.PublishFeedback(running, codec.EncodeFeedback(partial));
    if (done) server.Succeed(running, codec.EncodeResult(answer));
}
```

### The part worth knowing

Like parameters, **an action is not a primitive either** — it is *three services and two topics*
(`send_goal`, `cancel_goal`, `get_result`; `feedback`, `status`), plus a state machine deciding what
a goal is allowed to do next. A goal that has already succeeded cannot then be cancelled, and the
server has to say so rather than crash.

Three edges that are easy to get wrong and are handled here:

- **An all-zero goal id in a cancel means *everything currently running*.** That is what
  `send_goal` sends when you press Ctrl+C, so getting this wrong means Ctrl+C appears to do nothing.
- **`get_result` normally arrives *before* the goal finishes.** Clients ask the moment a goal is
  accepted and then wait. So those requests are **held and answered in order** when the result
  exists, not refused.
- **The service types are `package/action/Name_SendGoal`**, not `package/srv/...`. Our type splitter
  accepted only `srv` and rejected every action outright until that was fixed.

---

## Quality of Service

**Read this bit even if you skip the rest**, because it catches everyone once: if a publisher and a
subscriber disagree about QoS, **they simply don't connect**. No error, no warning, no messages.
Your publisher reports success, `ros2 topic list` shows the topic, and the subscriber sits there
forever.

The usual version: sensors publish best-effort, someone subscribes with the default (reliable), and
nothing arrives.

| Preset | Settings | For |
|---|---|---|
| `QosProfile.Default` | keep last 10, reliable, volatile | commands, state, anything you can't drop |
| `QosProfile.SensorData` | keep last 5, best effort, volatile | IMU, GNSS, LiDAR, cameras |
| `QosProfile.Latched` | keep last 1, reliable, transient local | maps, vessel descriptions, static transforms |

Diagnose a mismatch with:

```bash
ros2 topic info /your/topic --verbose
```

---

## Message types

The library itself has **no message classes at all** — it deals in `byte[]`. The classes live in
SEANAV, generated from the `.msg` files ROS ships:

```bash
python3 tools/ros_msggen.py --ros /opt/ros/jazzy --out <output folder>
```

That produces **351 types across 29 packages** — every message, plus services as `_Request` /
`_Response` pairs. Namespaces follow ROS, so `sensor_msgs/msg/Imu` becomes `sensor_msgs.msg.Imu`.

### Your own types

Lay a folder out the way a ROS package is laid out:

```
my_msgs/
  msg/MyThing.msg
  srv/DoSomething.srv
```

and point the generator at its parent:

```bash
python3 tools/ros_msggen.py --ros /opt/ros/jazzy --custom ./ros2 --out <output folder>
```

`my_msgs.msg.MyThing` then exists in C#. Nothing is compiled natively and no workspace is involved.

### Why the library doesn't carry them

An earlier version compiled SEANAV's message classes into `SeaNav.Ros2.dll` so the examples could
use them. Inside the Unity editor that was a disaster: Unity compiles those same files from
`Assets/`, so every type existed twice and the console filled with

```
error CS0433: The type 'IRosMessage' exists in both 'SeaNav.Core' and 'SeaNav.Ros2'
```

So the split is deliberate — **the binding carries the transport, SEANAV carries the format**, and
neither duplicates the other. The examples compile the CDR sources in themselves, which is why they
take a `SeaNavCore` property and the library does not.

Using this outside SEANAV? Bring your own encoder. Anything producing valid CDR will do.

---

## Testing

```bash
source /opt/ros/jazzy/setup.bash
./tools/interop_test.sh /path/to/SEANAV
```

Nine checks against ROS's own tools, which is the only test that means anything here — talking to
yourself proves nothing. It covers our publisher into `ros2 topic echo`, `ros2 topic pub` into our
subscriber, and `ros2 node list` / `ros2 topic info` seeing us as an ordinary node. It exits 3 and
says so if no ROS is sourced, rather than passing quietly.

### What was checked by hand, and what came back

Every feature claimed in [What works](#what-works) was run against stock ROS tooling. These are the
actual observed results, not a description of what should happen:

| Ran | Got back |
|---|---|
| `ros2 topic echo /seanav/imu` | our messages, decoded, correct fields |
| `ros2 topic pub` into our subscriber | received and decoded |
| `ros2 node list` / `topic info` / `service list` | we appear as an ordinary node |
| `ros2 service call /add_two_ints ... "{a: 41, b: 1}"` | `sum=42` |
| `ros2 param list` / `get` / `set` / `describe` | all four work |
| `ros2 param set` on a read-only parameter | refused, **with our reason shown by the CLI** |
| `ros2 action send_goal /fibonacci ... --feedback` | feedback streamed, `SUCCEEDED`, `0 1 1 2 3 5 8 13` |

Run the demos yourself:

```bash
dotnet run --project examples/ServiceDemo
dotnet run --project examples/ParameterDemo
dotnet run --project examples/ActionDemo
```

Then, in a second terminal with ROS sourced, poke at them with the commands in the table.

The CDR encoder has its own checks in SEANAV (`./tools/verify.sh Ros`), including the worked
byte example printed in the OMG specification. Those need no ROS at all.

---

## How it works inside

Worth knowing if you're debugging, and each of these cost real time to find.

**Libraries are opened by explicit path, not `[DllImport]`.** See
[Sourcing ROS](#sourcing-ros--read-this-if-nothing-works).

**Small interop structs declare real fields, never just `Size = 8`.** On 64-bit Linux a struct of
16 bytes or fewer comes back from a C function *in registers*, and which registers depends on the
field types. An empty struct doesn't say, so .NET returns it through memory instead and you read
garbage. Symptom: `rcl_get_zero_initialized_init_options` returned junk and the next call reported
`ALREADY_INIT`. Correct layout, wrong calling convention, and no error code can tell you which.

**Every rcl handle is pinned.** `rcl_node_init` keeps a *pointer* to the context and dereferences
it on every later call. Held in an ordinary field, that struct sits on the GC heap and the collector
can move it — leaving rcl with a dead address. Short runs passed; longer ones failed on the first
publish, with rcl blaming the *publisher* when the *context* had moved. Anything that looks like a
race with the garbage collector is one.

**`dlerror` is primed once at startup.** .NET resolves a P/Invoke stub on first call, and resolving
`dlerror` uses the dynamic loader — which clears the very error you were about to read. Before this
was fixed, every loader diagnostic came back blank.

**`rcl_get_error_string` is a macro**, not a function, pointing at `rcutils_get_error_string` in
`librcutils`. It returns a **1024-byte struct by value**, not a `char*`.

**Three endpoint option structs, three different sizes**: publisher 152, subscription 160, client
and service 128. They look interchangeable and are not.

---

## Porting to another distribution

Two things would need re-checking, and neither fails politely.

**The type support symbol naming** is a convention of ROS's code generator, not a guaranteed ABI.
It has held for every ROS 2 release so far. `RclInterop.MessageTypeSupport` and
`ServiceTypeSupport` are the only places that would need changing.

**The struct layouts** in `Native/RclInterop.cs` were **measured, not guessed** — a small C program
compiled against Jazzy's own headers printed `sizeof` and `offsetof` for each one. A wrong offset
doesn't give a wrong answer, it gives a crash or silent memory corruption. Re-measure before
claiming support for anything else.

---

## Known limits

- **No wait sets — polling only.** Nothing blocks until a message arrives; `rcl_wait` would be the
  way and isn't wired up. Fine for a simulator with its own loop, which is already running at a
  fixed rate; wasteful for an idle listener, which will spin a core doing nothing. Sleep in the
  loop, as the Listener example does.
- **Parameter callbacks are not wired.** You are told a parameter changed by reading it; there is
  no "on change" hook yet. The change topic is published so external tools see it.
- **No action result expiry.** A finished goal's result is kept for the life of the server rather
  than discarded after the ROS-conventional timeout. Harmless for a simulator run, a slow leak for
  a process running for weeks.
- **Linux x86-64 only** in practice. The Windows paths exist in the loader and are untested.

---

## Licence

Apache 2.0.

No code from `ros2cs` or `ros2-for-unity` is used here. This talks to the public ROS 2 C API, which
is the same API they talk to. Where their design taught us something, the source says so.
