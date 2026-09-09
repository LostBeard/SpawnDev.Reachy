# Changelog

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
