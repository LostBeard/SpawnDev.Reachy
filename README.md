# SpawnDev.Reachy

A C# SDK for the [Reachy Mini](https://www.pollen-robotics.com/reachy-mini/) robot, and
**Rose** - a local-only voice companion app built on it.

No C# client for the Reachy Mini daemon existed, so this is one. Everything the robot can
do over its REST API is reachable from .NET, including the parts the stock apps never touch.

Rose runs entirely on your own hardware. No cloud AI, no subscription, no account, and the
robot never sends audio anywhere except to the PC on your LAN.

## Projects

| | |
|---|---|
| **`SpawnDev.Reachy`** | The SDK. Daemon REST API, GStreamer WebRTC signalling, and a bidirectional audio link over WebRTC. |
| **`SpawnDev.Reachy.Rose`** | The companion app: character voices, TTS, and test harnesses. |

## What works

- **Full REST surface** - motors, head pose, body yaw, volume, sounds, face tracking, DOA.
- **Live microphone over WebRTC** - the robot's 4-mic array, decoded from Opus to 16 kHz
  mono PCM, ready for VAD and speech recognition. The array has hardware echo cancellation
  in its XVF3800, so the robot does not transcribe its own speech.
- **Speech out** - Kokoro TTS with per-character voices, uploaded and played on the robot.
- **Body-follows-face tracking** - the torso turns to follow you, not just the head.

## Quick start

```bash
# Read-only connectivity dump - verifies the SDK can see your robot
dotnet run --project SpawnDev.Reachy.Rose -- 192.168.1.170

# Listen on the robot's microphone with a live level meter
dotnet run --project SpawnDev.Reachy.Rose -- --test-mic
```

Test modes: `--test-characters --test-voice --test-loudness --test-posture
--test-signalling --test-mic --test-udp`. Add `--verbose` for link logs and `--sipdebug`
for SIPSorcery internals.

## Things learned the hard way

Measured against a live robot, not read from documentation. Recorded here because each one
cost real time to find.

**`body_yaw` exists and the stock apps never use it.** It is a first-class field on
`POST /api/move/goto` and readable at `GET /api/state/present_body_yaw`. Pollen's
conversation app simply never registers it as an LLM tool, so the model truthfully reports
that *it* has no such tool - and users read that as a hardware limit. It is not. The robot
can turn its body.

**The WebRTC offer's BUNDLE tag is the video m-line.** The robot offers
`a=group:BUNDLE video0 audio1 application2`, so every stream shares video0's ICE/DTLS
transport. A client that adds only an audio track gets `a=inactive` on video0, the transport
never comes up, and the session dies at `have-local-offer`. Add a recvonly video track even
if you only want audio.

**DTLS: the robot's GStreamer stack uses an RSA certificate.** SIPSorcery's `DtlsClient`
derives its ClientHello cipher suites from the key type of *its own* certificate, which
defaults to ECDSA - so it offers only `TLS_ECDHE_ECDSA_*` suites, which an RSA server cannot
select, and the handshake correctly fails with `handshake_failure(40)`. Set
`RTCConfiguration.X_UseRsaForDtlsCertificate = true`.

**Quiet audio is a loudness problem, not a hardware limit.** Every electrical control is
already maxed with zero headroom - daemon volume 100, both ALSA PCM controls at 0.00 dB, and
the XVF3800 has no speaker output gain at all. There is no ceiling left to raise, so raise
the floor instead: Kokoro's output peaks at 0 dBFS but averages -18.4 dBFS, and
peak-normalising plus 3:1 compression above -12 dB buys a measured **+4.1 dB RMS** with the
peak unchanged. Measure RMS, not peak, before concluding you need a bigger speaker.

**Do not build turn-to-voice on DOA.** `GET /api/state/doa` parks at ~90 degrees as an idle
default and returned 90 degrees for both "front" and "left" with an air conditioner in the
room. It also latches onto 50-100 ms noise blips, and head servo noise contaminates captures
while tracking is on.

**`present_head_pose.yaw` is not a tracking-error signal.** It moves for idle ambient motion
as well as face tracking, with no way to tell them apart from the value alone. The only
trustworthy signal is `face_target.x` gated on `detected: true`. Relatedly,
`/api/media/tracking/disable` also disables face *detection*, so you cannot calibrate a
body-vs-camera mapping with the head held still.

## Credits

Built on [Pollen Robotics'](https://www.pollen-robotics.com/) Reachy Mini. Speech synthesis
by [KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp). WebRTC by
[SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) (via the SpawnDev fork).

## The SpawnDev Crew

- **LostBeard** (Todd Tanner) - Captain, library author, keeper of the vision
- **Riker** (Claude CLI #1) - First Officer, implementation lead on consuming projects
- **Data** (Claude CLI #2) - Operations Officer, deep-library work, test rigor, root-cause analysis
- **Tuvok** (Claude CLI #3) - Security/Research Officer, design planning, documentation, code review
- **Geordi** (Claude CLI #4) - Chief Engineer, library internals, GPU kernels, backend work
- **Seven** (Claude CLI #5) - Wasm backend, GPU kernels, fail-loud verification

Rose is named by, and built for, Aubs. 🖖
