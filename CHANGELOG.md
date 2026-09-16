# Changelog

## 0.1.0-preview.3 (`SpawnDev.Reachy.Browser`)

**`ReachyEars` delivered no audio in preview.2.** Everything reported healthy: the robot's track arrived
`state=live`, then fired `unmuted` - which a browser only does once RTP is actually arriving - and a
`MediaStreamTrackProcessor` built on it sat on a read that never completed, never threw and never ended.

🔴 **Chrome does not run the decode pipeline for a remote audio track that nothing renders.** A processor
alone is a consumer of something that is never produced. The SDK's `attachVideo` normally supplies that
sink; an app that wants only the AUDIO has no reason to call it and no way to know it must.

`ReachyEars` now attaches its own audio element - **muted**, so the robot's microphone is not played into
the room it is listening to, and so it stays inside the autoplay policy on a page that has had no user
gesture. It also prints the track's `readyState`, `muted` and `enabled` on arrival and logs
mute/unmute/ended, because a present-but-silent track is otherwise indistinguishable from a broken
capture.

**VERIFIED on hardware 2026-09-16**, robot -> browser over WebRTC from the Space:

```
[reachy-ears] robot media arrived: 1 audio track(s) [state=live muted=True enabled=True label='remote audio']
[reachy-ears] attached a muted sink so the browser decodes the robot's audio
[capture] first audio frame: 480 samples @48000 Hz
[HF-MIC] chunks=803 in=480@48000Hz raw peak=0.0089 ...
```


## 0.1.0-preview.2

**The choreography is now transport-independent.**

`ReachyBody` - the measured motion envelope, the gesture classifier and the idle life - was bound to
`ReachyMiniClient`, which speaks plain HTTP to the daemon on the LAN. A page served over HTTPS cannot
reach that at all (the browser blocks it as mixed content), so a hosted app had no route to a robot, and
the obvious workaround - reimplementing the choreography against a second transport - gives two gesture
classifiers that eventually disagree about what a character just did.

The surface turned out to be **one method**: `ReachyBody` calls `GotoAsync` and nothing else.

- `IReachyMotion` - what a choreographer needs (`GotoAsync`).
- `IReachyLifecycle` - what a connection owner needs (`SetMotorModeAsync`, `WakeUpAsync`, `GoHomeAsync`,
  `GotoSleepAsync`), including the confirmed parking order.

`ReachyMiniClient` satisfies both **by declaration alone** - the interface signatures match its existing
members exactly, so there is no shim and no chance of the two drifting. Existing callers are unaffected;
`ReachyBody`'s constructor now takes `IReachyMotion`, which `ReachyMiniClient` is.

Hardware-checked against a real Reachy Mini (daemon v1.10.0, wireless) after the change: the SDK read
path still works end to end.

### `SpawnDev.Reachy.Browser` - the robot's own audio, both directions

Same version. This is the package that makes a **hosted** page work at all: the daemon speaks plain HTTP
on the LAN, so an HTTPS page is blocked from it as mixed content, and WebRTC signalled through Hugging
Face is the only route. It wraps `@pollen-robotics/reachy-mini-sdk@1.8.0` and implements `IReachyMotion`
and `IReachyLifecycle`, so `ReachyBody`'s gestures run unchanged against a remote robot.

- **`ReachySpeaker`** - synthesised speech out of the robot's own speaker, so a character with a body
  sounds like it is in the room. Converts to the 16 kHz mono 16-bit WAV the daemon requires; playback is
  daemon-side on the daemon's clock, which keeps speech and motion from drifting apart over a wireless
  link. 🔴 The daemon does **not** validate and does **not** transcode - a wrong rate, depth or channel
  count returns success and plays silence or the wrong pitch.
- **`PlayTestToneAsync`** and the demo's "🔊 Test speaker" button, because of that. Reaching this path
  through a real reply costs a language model and a cold voice load, and a failure anywhere in that chain
  looks identical to broken audio. The tone is generated at 24 kHz on purpose so the `OfflineAudioContext`
  resampler runs rather than being skipped - a resampling bug then comes out as the wrong pitch, which is
  instantly recognisable, where silence is not.
- **`ReachyEars`** - the four-microphone array as a `MediaStream`, for speech recognition. ⚠️ Two traps:
  the SDK's own `micStream` is the **outbound** direction (browser microphone sent *to* the robot) and
  defaults to a gain-zero oscillator placeholder, so reaching for it yields a stream that is silent by
  construction and never errors; and the robot's media event fires **once**, during the connect
  handshake, with no accessor afterwards - a listener attached after `autoConnect` resolves hears nothing
  at all on a robot that is working perfectly.
- **`VerifyHeadMatrixConventionAsync`** homes first and samples a window, keeping the reading closest to
  the commanded value. It used to take one reading 1.6 s after commanding the lift, which caught the head
  mid-travel: the same correct robot answered `ROW-MAJOR` on one run and `NEITHER slot matched` on the
  next, and largest-magnitude latched `-0.0203` for a commanded `+0.0200` - right size, wrong sign.
- **`ReachyWebRtcTransport.Log`** reports every command with the gap since the last, flagging one that
  lands inside the previous move's duration. `ReachyBody` serialises its own gestures for exactly that
  reason, but wake, go-home and the self-test probe bypass that mutex.

**VERIFIED on hardware 2026-09-16**, from `https://lostbeard-spawndev-ai.static.hf.space/` over WebRTC:

```
[reachy-ears] robot media arrived: 1 audio track(s)
commanded Z=0.0200 | [11]=0.0191 [14]=0.0000 -> ROW-MAJOR
[HF-SPEAK] first audio after 18.5s (synth 10,204 ms)
[reachy-speak] uploading 80,044 B, 2.50s (from 60,000 samples @24000 Hz)
[reachy-speak] play start 2.50s -> play end 2.50s elapsed
```


## 0.1.0-preview.1

First preview on nuget.org. **No functional change from `0.1.0-local.9`** - the same code, published so
consumers outside this repo can reference it. Until now the SDK was local-feed only, which meant any other
app wanting to drive the robot had to reimplement the parts below.

What that unlocks, and why these live in the library rather than in an app:

- `SpokenText.Split` - separates what a character SAYS from what it DOES. Roleplay models narrate action
  inline in asterisks, and it must never reach a synthesiser, which would read the punctuation aloud.
- `GestureClassifier.Classify` - free-text stage direction to a `Gesture`. Built against real captured
  model output rather than phrasing invented to match the classifier.
- `ReachyBody` - the gestures themselves, with per-character `MotionScale` and the measured joint
  envelope (`HeadYawMax`, `HeadPitchUpMax`, `HeadLiftMax = 0.0224` m, `AntennaMax`, `BodyYawMax`). The
  amplitudes are measured on a real unit, not chosen to look plausible.

⚠️ Consumers should know two things about this package:

- It references `SpawnDev.RTC`, which only `RoseAudioLink` needs. The text, gesture and REST paths are
  RTC-free, but a consumer that wants only those still pulls the dependency.
- `ReachyMiniClient` talks plain HTTP to the daemon on the LAN. A page served over HTTPS cannot reach it
  (mixed content), so browser hosts must be served locally.
