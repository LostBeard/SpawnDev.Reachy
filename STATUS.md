# Rose - status and next steps

**2026-07-20.** Solution builds clean. Pushed: https://github.com/LostBeard/SpawnDev.Reachy

## She listens and answers

`dotnet run --project SpawnDev.Reachy.Rose -- --talk`

Microphone -> Silero VAD -> Whisper base.en -> llama3.1:8b -> Kokoro -> the robot's own
speaker. About a second from the end of a question to the start of the answer.

Three defects were found by testing and fixed, each of which Aubs would have hit in her
first minute:

- **Rose interrupted herself** a word or two into every sentence. `play_sound` only queues
  and returns, so each streamed sentence cut off the one before it. Synthesis and playback
  are now separate: the next line renders while the current one plays, but playback is
  strictly serialized. Play start/end is logged so overlap is checkable from data.
- **Character switching failed more often than it worked.** Whisper hears the names as
  ordinary words - "using", "gone", "sad", "dull", "an". Measuring that across three voices
  took it from 10/21 to 21/21. Since several are real English words they only match right
  after a switch cue, so "I want an ice cream" is not a request to become N.
- **An ellipsis split sentences**, turning "It's so... sparkly!" into two clips with a gap.
  These characters use "..." constantly.

The model is loaded during connect (first reply 6.4s -> 1.0s) and released on exit
(VRAM 6299 -> 1100 MiB, verified) rather than idling out on TJ's workstation GPU.

**Not yet verified: a real conversation with a real child's voice.** Everything above was
tested with synthesised speech. Aubs's voice will be misheard differently - re-run
`--test-names` with her and extend each character's `Mishearings` from what it reports.

Rose = Aubs's Reachy Mini Wireless (she named it; originally "Vertex").
Goal: local-only Murder Drones roleplay, favourite character **Serial Designation N**.

| | |
|---|---|
| Robot | `192.168.1.170` (daemon v1.9.0, hw id `bb68ce143f77ba8d`) |
| Dev PC | `192.168.1.120` - RTX 4070, build here first |
| Aubs's PC | `192.168.1.168` - RTX 2060 12GB, eventual target |

Robot parked: centred, `goto_sleep`, motors **disabled**, tracking off, 0 loop errors.

---

## Projects

- **`SpawnDev.Reachy`** - C# SDK for the daemon REST API + WebRTC signalling + audio
  link. Verified live. No C# Reachy client exists anywhere - publishable as-is.
- **`SpawnDev.Reachy.Rose`** - Aubs's app: character library, TTS, test harnesses.

### Test modes (`dotnet run --project SpawnDev.Reachy.Rose -- <mode> [ip]`)

| mode | does |
|---|---|
| *(none)* | read-only SDK connectivity dump |
| `--test-characters` | character name resolution, 15/15 passing |
| `--test-voice` | speaks one line per character through Rose |
| `--test-loudness` | A/B raw vs compressed, prints measured dB |
| `--test-posture` | A/B head-down vs head-up |
| `--test-signalling` | WebRTC signalling + SDP offer |
| `--test-mic` | full audio link + live level meter |
| `--test-udp` | proves inbound UDP reaches THIS binary |
| `--reflect-tts` / `--reflect-cert` | dump third-party API surfaces |

Add `--verbose` for link logs, `--sipdebug` for SIPSorcery internals.

---

## Working

- **LLM** `llama3.1:8b` via Ollama. Warm: **TTFT 0.16s**, 68 tok/s. Best in-character
  answer with real show knowledge. Fits the 2060 later. Start Ollama:
  `~/AppData/Local/Programs/Ollama/ollama.exe serve`
- **TTS** KokoroSharp.GPU, 0.9s load, ~1s/line, 54 voices, 7 characters mapped.
  `kokoro.onnx` (310MB) sits in the solution root - **gitignore it**.
- **Speech out** upload + `play_sound`, with auto head-lift and +4.1 dB compression.
- **Body-follows-face tracking** `scratchpad/rose_body_track.cs` (TJ-confirmed working).

## WebRTC microphone - SOLVED 2026-07-20

The 4-mic array streams live. Full chain: signalling -> SDP -> ICE -> DTLS -> SRTP ->
Opus -> 16kHz mono PCM. Measured `--test-mic` run: **994 packets, 318080 samples = 19.9s**,
levels moving between -42.5 and -30.3 dBFS.

Root cause of the old `handshake_failure(40)` was a certificate/cipher-suite mismatch, and
the alert was the robot correctly refusing an impossible ClientHello:

- The robot's GStreamer stack generates an **RSA 2048** DTLS certificate
  (`sha256WithRSAEncryption`), and it is the DTLS server. Dumped live off the robot by
  asking its own `dtlsdec` element for the `pem` it would use.
- SIPSorcery's `DtlsClient` picks its ClientHello cipher suites from the key type of **its
  own** certificate, which defaults to ECDSA - so it advertised only the five
  `TLS_ECDHE_ECDSA_*` suites. An RSA server cannot select any of those.

Fix is one line in `RoseAudioLink`: `X_UseRsaForDtlsCertificate = true`. It drives both the
self-signed cert generation and the offered suite list, so the SDP fingerprint stays
consistent.

The `certificates2`-needs-BouncyCastle-types lead was a dead end and had nothing to do with
the failure.

**Still open, library side:** deriving the offered suite list from our own cert type is
wrong in principle - in TLS 1.2 the suite's auth algorithm constrains the *server's* cert,
and browsers advertise both families in one ClientHello. The fork should offer the union of
`ECDHE_ECDSA` + `ECDHE_RSA`. Reported to Riker (his lane):
`_DevComms/global/data-TO-riker-sipsorcery-ecdsa-only-clienthello-breaks-gstreamer-2026-07-20.md`.
Until that lands, the flag makes us present RSA to browsers too - legal, but not the
long-term default.

**Next on the mic:** VAD + Whisper, then close the loop mic -> STT -> llama3.1:8b ->
Kokoro -> speaker.

---

## Volume - SOLVED by output loudness processing

The original complaint - Rose far quieter than expected, head up, at stock settings -
is fixed by `RoseVoice.Loudify`. TJ confirmed audibly louder.

Every hardware control was already maxed with **zero headroom**: daemon volume 100, both
ALSA PCM controls at **0.00 dB**, and the XVF3800 parameter table has no speaker output
gain at all (only capture-side `AUDIO_MGR_MIC_GAIN` / `PP_AGC*`, plus
`AUDIO_MGR_REF_GAIN` which is the AEC loopback reference). The ceiling could not move.

So raise the **floor**. Kokoro peaks at -0.0 dBFS but averages -18.4 dBFS. Peak-normalise
+ 3:1 compression above -12 dB with makeup gain gives a **measured +4.1 dB RMS**
(-18.4 -> -14.3 dBFS) with the peak unchanged. Runs on raw PCM before upload.

The lesson worth keeping: every control reading "maxed" made this look like a hardware
limit. It was not - it was a *peak* measurement hiding a low *average*. Measure RMS.

Separately, `LiftHeadToSpeak` raises the head to `z = 0.0224` before speaking. The
speaker fires upward from the chest, so a head parked in the sleep pose muffles it. That
only matters when the robot is head-down and was **not** the cause of the original
complaint - but the lift is correct behaviour anyway and costs nothing.

---

## Voice cloning (next feature after the mic)

TJ has S01. Pre-extracted to `scratchpad/md_audio/`: 8 `.srt` (English CC) + 8 `.wav`
(16kHz mono), ~338MB. Source: `V:\Video\Series\Murder Drones\S01`.

CC speaker labels are `[Uzi]` bracket form and **sparse** (Tessa 36, Uzi 16, N 5, J 4,
V 3) - CC only labels when ambiguous. That is still enough: use the labelled lines as
**seed voiceprints**, diarize the rest, match by cosine similarity. No manual labelling.
Capitalised brackets = names; lowercase (`[sighs]`) = sound cues, which mark segments to
exclude. Pipeline is all C#: ffmpeg 8.1 (on PATH) + sherpa-onnx
(`org.k2fsa.sherpa.onnx` - diarization, source separation, VAD).

Agreed boundary: private home use is fine; do **not** distribute audio impersonating the
real voice actors.

---

## Dead ends - do not re-run

- **DOA turn-to-voice.** `/api/state/doa` parks at ~90 deg as an idle default and gave
  90 deg for BOTH "front" and "left" with the AC on. Latches on 50-100ms noise blips.
  Head-servo noise contaminates captures while tracking is on.
- **Neck controller driven by `head_yaw`.** That value moves for idle motion as well as
  tracking with no way to tell them apart. The face-x tracker is correct.
- **Auto-calibrating the face-x gain.** Impossible with tracking on (the head cancels the
  probe: same probe gave dx 0.119 then 0.047) and impossible with it off
  (`tracking/disable` also kills face DETECTION). Use the fixed -1.5 constant.
- **Firewall as the WebRTC cause.** Inbound UDP to the Rose binary works - proven with
  `--test-udp`. Windows inbound rules are per-program, so test from the real binary.

## Diagnostics that work

- Robot: `ssh pollen@reachy-mini.local` (password `root`),
  `journalctl -u reachy-mini-daemon --since '-5 min' --no-pager`.
  Helper: `scratchpad/rose_ssh.cs "<cmd>"`. The `/logs` HTTP endpoint is a dead page.
- Client: `--sipdebug` turns on SIPSorcery's internal logging and names the real failure.

## Housekeeping for tomorrow

- `.gitignore` for `kokoro.onnx`, `bin/`, `obj/`
- Commit and push - nothing is in git yet
- Fold `scratchpad/rose_body_track.cs` into the SDK as a proper `FaceTracker` class
- Credit the SpawnDev crew in the README
