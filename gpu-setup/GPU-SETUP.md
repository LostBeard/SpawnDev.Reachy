# GPU voice cloning (CUDA) - setup

Runs ZipVoice voice cloning on the NVIDIA GPU instead of the CPU. Measured on TJ's
RTX 4070: a line that took **~9s on CPU renders in ~1.2s cold and under a second
warm** - roughly a 7-8x speedup. The voice is bit-identical; only the speed changes
(same fp32 / 16-step recipe, same pitch guard).

This is on by default: `RoseVoice` asks for the `cuda` provider, and sherpa quietly
falls back to CPU if the GPU stack below is missing, so nothing breaks without it.

## Why it needs a few extra files

The clone engine is sherpa-onnx ZipVoice, which runs on onnxruntime. Unlike
SpawnDev.ILGPU (which JITs PTX straight through the CUDA driver and needs no
redistributables), onnxruntime's CUDA execution provider dynamically loads the CUDA
runtime + cuDNN. sherpa 1.13.4 is ABI-linked to onnxruntime **1.27.0**, and
onnxruntime 1.27 is a **CUDA 13 + cuDNN 9** build. So the pieces are:

| Piece | Source | Already present? |
|-------|--------|------------------|
| onnxruntime GPU 1.27.0 (`onnxruntime.dll` + `onnxruntime_providers_*.dll`) | NuGet `Microsoft.ML.OnnxRuntime.Gpu.Windows` 1.27.0 | yes - restored by the build |
| CUDA 13 runtime (`cudart64_13`, `cublas64_13`, `cublasLt64_13`, `cufft64_12`) | CUDA 13.x toolkit, its `bin\x64` on PATH | **must be installed** |
| cuDNN 9 for CUDA 13 (`cudnn64_9.dll` + 9 sub-libraries) | `cudnn-9.25-cuda13.zip` here | extracted into `gpu-runtime/cudnn` |
| `zlibwapi.dll` (cuDNN delay-loads it) | `zlib123dllx64.zip` here | extracted into `gpu-runtime/cudnn` |

The build copies everything in `SpawnDev.Reachy.Rose/gpu-runtime/cudnn` next to
onnxruntime in the output, and the app prepends that folder to its own PATH at
startup so the CUDA provider can find cuDNN (onnxruntime searches the exe folder and
PATH, not the `runtimes/win-x64/native` subfolder where these land).

## Set it up on a fresh machine (e.g. Aubs's PC)

1. **Install the NVIDIA CUDA 13.x toolkit** (13.2 is what this was built against).
   The installer puts `...\CUDA\v13.x\bin\x64` on PATH, which provides cudart/cublas.
   The GPU driver must be recent enough for CUDA 13 (TJ's is 596.21).

2. **Recreate the cuDNN folder** from the saved zips (both are in this folder):

   ```
   dotnet run gpu-setup/extract-cudnn.cs
   ```

   This extracts cuDNN 9's DLLs + `zlibwapi.dll` into
   `SpawnDev.Reachy.Rose/gpu-runtime/cudnn` (~540 MB, gitignored).

3. **Build.** `dotnet build -c Release` copies the cuDNN DLLs next to onnxruntime.

4. **Confirm the GPU is actually used:**

   ```
   dotnet run -c Release --project SpawnDev.Reachy.Rose --no-build -- \
     --test-clone --ref=models/voiceprints/N.wav --fp32 --steps=16 --gpu \
     --say="Testing on the graphics card." --out=scratchpad/gpu_test.wav
   ```

   A ~1s render (vs ~9s) and GPU utilization on `nvidia-smi` mean it worked. If you
   instead see `Cannot load symbol cudnnCreate`, cuDNN or zlibwapi isn't reachable -
   re-run step 2 and rebuild. sherpa prints its provider line when `--gpu` is set.

## Where the sources came from

- cuDNN 9.25 for CUDA 13 (Windows): NVIDIA redist
  `https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.25.0.15_cuda13-archive.zip`
- `zlibwapi.dll`: the canonical WINAPI zlib build NVIDIA's own cuDNN docs point to,
  `http://www.winimage.com/zLibDll/zlib123dllx64.zip`.

## Toggles

- Any test mode: add `--gpu` (or `--cuda`) to use the GPU; `--test-voiceprints` uses
  the GPU by default, add `--cpu` to force the processor for an A/B.
- In code, `new RoseVoice(..., cloneProvider: "cuda" | "cpu")` and
  `new RoseVoiceClone(modelDir, fp32, steps, provider)`.
