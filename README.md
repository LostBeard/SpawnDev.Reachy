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

- **You can talk to her.** Microphone to speech recognition to language model to speech
  synthesis to the robot's speaker, closed loop, roughly a second from the end of your
  sentence to the start of hers.
- **Seven characters**, switchable out loud - "can you be Uzi?" - each with its own voice,
  personality, resting antenna posture and movement size.
- **She moves while she talks.** The model narrates its own actions
  (`*antennas twitch excitedly*`), and those drive the real servos - head, antennas and
  the torso the stock app never turns.
- **Full REST surface** - motors, head pose, body yaw, volume, sounds, face tracking, DOA.
- **Live microphone over WebRTC** - the robot's 4-mic array, decoded from Opus to 16 kHz
  mono PCM. The array has hardware echo cancellation in its XVF3800, so the robot does not
  transcribe its own speech.
- **Body-follows-face tracking** - the torso turns to follow you, not just the head.

## Quick start

Needs [Ollama](https://ollama.com) running with `llama3.1:8b` pulled.

```bash
# One-time: fetch the speech models (~250MB, not in git)
dotnet run tools/fetch_models.cs

# Read-only connectivity dump - verifies the SDK can see your robot
dotnet run --project SpawnDev.Reachy.Rose -- 192.168.1.170

# Talk to her
dotnet run --project SpawnDev.Reachy.Rose -- --talk
```

### Test modes

Run these instead of asking a child to find your bugs for you.

| mode | does |
|---|---|
| `--talk` | the live conversation loop |
| `--test-loop` | the whole chain end to end with a synthesised question standing in for a person |
| `--test-ears <file.wav>` | VAD + transcription on a file, no robot needed |
| `--test-brain` | the language model alone, with latency per reply |
| `--test-names` | what recognition ACTUALLY returns for each character name |
| `--test-verify` | whether Rose listening to her own render catches a garbled one, and what the check costs |
| `--test-speech` | sentence splitting, action stripping, switch-intent gating |
| `--test-body` | every gesture, driven by real stage directions the model produced |
| `--park` | the shutdown sequence alone, with the head pose at each stage (`--no-home` for the old behaviour) |
| `--probe-limits` | measures the real joint travel by commanding past it and reading back |
| `--test-mic` | audio link with a live level meter |
| `--test-voice` / `--test-loudness` / `--test-posture` | speech output, compression A/B, head position A/B |
| `--test-characters` / `--test-signalling` / `--test-udp` | name resolution, WebRTC signalling, inbound UDP |

Add `--verbose` for link logs and `--sipdebug` for SIPSorcery internals.

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

**Speech recognition does not know these names, and the failures are systematic.**
Whisper renders "Uzi" as "using", "Khan" as "gone", "Thad" as "sad" and "Doll" as "dull" -
consistently, across every voice tested. Measured rather than guessed, that took character
switching from 10/21 to 21/21. Since several of those are ordinary English words, they are
only matched in the slot immediately after a switch cue, so "I want an ice cream" does not
turn the robot into a different character. `--test-names` re-measures it.

**The cloner garbles an occasional noise draw, and the fix is to let her hear herself.**
Zero-shot ZipVoice draws fresh noise for every render, so the same sentence can come back
as a different sentence - it is a property of the model, not of the words it was given, and
no reference or phoneme tuning removes it. Every cloned line is now transcribed, scored
against the words it was asked to say, and drawn again if they do not match. `--no-verify`
turns it off for an A/B by ear and `--test-verify` re-measures all of it.

**The recogniser doing the checking must be the WEAKER one, which is the opposite of the
obvious design.** Sharing the microphone's small.en model looks free and is wrong. Given
the same captured clip - a render that had collapsed into a repetition loop - small.en
wrote back *"Hey, can we talk about something else, please?"* and scored it **0%**, while
base.en wrote *"Can Can Can We Can We Can We Can We Can"* and scored it **75%**. small.en is
good enough to reconstruct the sentence that was meant, so it repairs the exact defect the
check is looking for. A model that can fix the failure cannot detect it. The self-check runs
its own base.en, which is also 3x cheaper (345ms vs 1095ms per clip, about **+320ms per
line**) and is a ~75MB CPU model that never touches VRAM.

**Keep the bad renders.** A garble happens on a few percent of draws and cannot be summoned
on demand, so every one is saved to `models/garbled/` with the text it should have been, and
every later run replays all of them. Without that, "no garble happened" and "the check has
gone blind" produce identical clean output - which is exactly how the small.en problem above
hid behind a run reporting 0 garbles in 96 draws.

**A word-error check on synthesised speech does NOT floor at zero, so the tolerance has to
clear the floor.** Renders that sound perfect score 8-15% when the line carries a name the
recogniser does not know, and 0% when it does not. The threshold sits at 20% for that
reason. Two consequences worth knowing: a render rejected by the pitch guard is never
transcribed at all, so reporting its word error as 0% would be an unmeasured zero wearing a
perfect score; and a line whose floor is genuinely above tolerance would otherwise pay the
full re-roll loop every time it was spoken, so a render that fails every draw is still
remembered for the session - just never written to the durable cache, where a bad draw would
be replayed forever.

**Anything measuring a transcript needs a control sentence with no compound words.** The
first control here ended "near the river bank", the recogniser wrote "riverbank", and a
flawless render scored 15% - a floor under the instrument that had nothing to do with the
audio. Changing the sentence took the same control to 0%.

**sherpa-onnx resamples inside `AcceptWaveform`.** The synthesiser renders at 24kHz and the
recogniser wants 16kHz; passing the clip's true rate is enough, and it logs
`Creating a resampler: in_sample_rate: 24000 output_sample_rate: 16000` when it does. No
resampling code is needed on this side - which is worth knowing before writing one.

**Park via HOME, then sleep.** `goto_sleep` starts from wherever the robot IS, and speaking leaves the
head LIFTED clear of the chest speaker - sleeping from there throws the head back on the way down. Going
home first (head centred and level, antennas neutral, torso square) means it lowers straight into the
chest. Measured with `--park`: lifted Z=+0.0216 -> home 0.0000 -> asleep Z=-0.0439 pitch +0.446, and
cutting motor power from the sleep pose relaxes only **3.2 degrees**. Home is a way station on the path to
sleep, never a substitute - a neutral head-up pose is not mechanically stable and the head drops.
`--park --no-home` reproduces the old behaviour.

**The pitch guard, not the word check, is what costs re-draws.** Measured over 48 lines: 47 draws thrown
away, **46 for pitch and 1 for words** - and across a whole live conversation the word check rejected
nothing at all. Speaking verification costs a transcription per line (~390ms) and almost never forces a
second render. If render latency needs to come down, the pitch band is the lever; the word check is not.
`--test-verify` prints the split, so this is checkable rather than assumed.

**`play_sound` only queues.** It returns as soon as playback is accepted, not when it
finishes, so starting the next clip cuts the previous one off. A reply synthesised sentence
by sentence will interrupt itself a word or two into every line unless playback is
explicitly serialized. Synthesis of the next line can still overlap the current one - that
is what keeps it gapless.

**Roleplay models narrate their actions inline**, in asterisks: `*antennas twitch* Wait,
really?!`. That must never reach the synthesiser, which reads the punctuation out loud.
Split rather than delete - the robot really does have a head, antennas and a rotating
torso, so the stage direction is a free movement cue the model generates unprompted.

**The joint limits are not what you would guess, and the daemon clamps silently.** An
out-of-range `goto` returns success and simply does not go there, so gesture code written
against an imagined range looks half-finished rather than failing. Measured with
`--probe-limits`:

| axis | limit |
|---|---|
| head yaw | > 1.55 rad (no clamp found) |
| head pitch, down | ~0.68 rad |
| head pitch, up | ~0.51 rad - noticeably less than down |
| head roll | ~0.70 rad |
| head lift (Z) | **0.0224 m** - hard stop |
| antennas | ~3.1 rad - by far the largest range |
| body yaw | ~0.98 rad, and additionally constrained to ~65 degrees of head yaw |

Turning the head the same way first therefore buys extra torso travel.

**Classify a stage direction by which cue appears EARLIEST, not by keyword priority.** The
model writes the primary action first and qualifies it after. "Antennas twitch excitedly as
the torso rotates" is an antenna twitch that happens to be excited; "I bob my torso up and
down enthusiastically, my antennas wiggling" is a bob that happens to involve antennas.
Position separates those; a fixed priority order gets one of them wrong whichever way you
sort it. Bare body-part nouns ("head") are the exception - they are almost always the first
word, so they only apply as a last-resort fallback.

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
