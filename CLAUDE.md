# SpawnDev.Reachy - Project Rules

Global rules: `D:\users\tj\Projects\CLAUDE.md`. Read those first.

**What it is:** a C# SDK for the [Reachy Mini](https://www.pollen-robotics.com/reachy-mini/) daemon, plus **Rose**, a local-only voice companion built on it. No cloud, no account - audio never leaves the LAN.

| Project | |
|---|---|
| `SpawnDev.Reachy` | The SDK. Daemon REST API, GStreamer WebRTC signalling, bidirectional audio over WebRTC. |
| `SpawnDev.Reachy.Rose` | The companion app: character voices, TTS, test harnesses. |

Needs Ollama with `llama3.1:8b`. Speech models (~250MB, not in git): `dotnet run tools/fetch_models.cs`.

## Test modes - use these, do not ask a person to find your bugs

| mode | does |
|---|---|
| `--talk` | the live conversation loop |
| `--test-loop` | whole chain end to end, synthesised question standing in for a person |
| `--test-ears <file.wav>` | VAD + transcription on a file, no robot needed |
| `--test-brain` | the language model alone, latency per reply |
| `--test-names` | what recognition ACTUALLY returns for each character name |

Read-only connectivity dump: `dotnet run --project SpawnDev.Reachy.Rose -- <robot-ip>`.

## 🔴 A setting TJ or Aubs confirmed BY EAR is LOCKED

Never trade it for speed, latency, or convenience. Two silent compromises brought back an echo TJ had already paid to fix, and he heard it immediately: dropping ZipVoice 16 steps to 4, and routing cloned audio through `Loudify` (compressor makeup gain lifts the low-level tail into reverb).

**Adding an EXISTING post-process to a NEW signal path is a recipe change too**, even when no number moves.

⚠️ When a symptom is one he has had fixed before, **diff your changes against the written recipe FIRST.** A 30-second diff named both causes; a whole diagnostic mode did not. Latency is solved by pre-generation and caching, never by lowering quality.

## 🔴 Parking the robot - the confirmed recipe

**GO HOME FIRST**, then `goto_sleep`, wait for the head to stop, THEN motors off.

`goto_sleep` starts from wherever the robot IS, and speaking leaves the head LIFTED - sleeping from there throws the head back. From home it lowers into the chest. Motors-off from sleep relaxes only 3.2 degrees, so a neutral head-up pose is NOT stable. `--park` re-measures; `--no-home` reproduces the old bad behaviour.

## 🔴 Audio and speech traps

**`play_sound` on the daemon only QUEUES and returns.** Fire-and-forget makes sentence-at-a-time streamed TTS interrupt ITSELF a word or two in. Split synthesise (`PrepareAsync`) from play (`PlayAsync` + wait duration): render ahead, play strictly serialized. **LOG play start/end** so non-overlap is verifiable from the timeline, not only by ear - TJ heard this defect before I did because I had no instrument for it.

**MEASURE what ASR actually returns for proper nouns; never match on spelling.** Whisper base.en consistently gives Uzi -> "using", Khan -> "gone", Thad -> "sad", Doll -> "dull", N -> "an". Character switching was 10/21 by spelling and 21/21 against a measured `Mishearings` table. ⚠️ Several mishearings are common words ("an", "gone", "dull") - match them ONLY in the slot right after an explicit cue ("can you be ___"), or "I want **an** ice cream" switches character. Unit-test BOTH directions, and ask AS a different character so every case must actually switch.

**A transcript is the RECOGNISER'S output, never the utterance.** Recognition returned "and you'll be n"; the child had said it perfectly and Whisper destroyed the cue phrase. That changed the engineering conclusion (the bare `be ` fallback is load-bearing) AND wrongly blamed a person in writing. ⭐ If a human was in the room they outrank the instrument - ASK.

**When a model GRADES another model's output, the MORE capable grader is the wrong one** - it applies a language prior and REPAIRS the defect. Same clip: small.en heard a garbled render as the intended sentence (0% detected), base.en heard the actual garble (75%). Pick the weakest grader that reads clean input correctly. Only visible with POSITIVE CONTROLS - blind runs saying "0 garbles in 96" is identical output to "the grader is blind."

**Pick a resampler fixture from the PROPERTY, not from realism.** `Resample()` had no anti-aliasing filter, so 48 kHz mic audio decimated to 16 kHz folded 8-24 kHz onto the speech and Whisper returned fluent unrelated text. A test DID call it at 44100->16000 but asserted only length and range, on a 440 Hz tone with nothing to alias - real code, could not fail.

## ⚠️ WebRTC / DTLS

`handshake_failure(40)` AFTER ICE connects is a cert KEY TYPE vs advertised CIPHER SUITE mismatch. Do not theorize MTU, timeouts, or .NET cert types.

The Reachy Mini's GStreamer stack serves an **RSA-2048** cert; our `DtlsClient.GetSupportedCipherSuites()` branched on OUR OWN cert type (default ecdsa) and advertised only `TLS_ECDHE_ECDSA_*`. Fix: `RTCConfiguration.X_UseRsaForDtlsCertificate = true` - mic came up instantly. Dump the peer's cert key type from its own stack (`dtlsdec`'s `pem` property over ssh, then `openssl x509`).

The real fork source is `SpawnDev.RTC\SpawnDev.RTC\Src\sipsorcery\`, NOT the `sipsorcery-master` upstream reference checkout.

The robot's 4-mic array has hardware echo cancellation in its XVF3800, so the robot does not transcribe its own speech.
