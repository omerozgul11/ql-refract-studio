# Refraction Studio training video

A narrated, captioned walkthrough of the complete workflow in the real application (about six
minutes, 1920 × 1080, H.264/AAC), published as `download/Refraction-Studio-Training-<version>.mp4`
with an `.srt` caption file and a chapter list, and inside the client package.

| File | Role |
|---|---|
| `storyboard.json` | Per segment: the caption text, the spoken text (phonetic spellings for the voice), the chapter and the actions to film |
| `common.py` | Paths and the caption sentence splitter |
| `narrate.py` | Offline Kokoro voice (`af_heart`, Apache-2.0 model in `.tools/media/models`, see below); re-voices only changed segments |
| `capture.py` | Starts a separate, empty Refraction Studio (Linux build, container `rfs-capture`, 1920 × 960, no network) with synthetic field records, drives it with real pointer and keyboard events and records each segment with ffmpeg. Each segment lasts at least as long as its narration; `cut` / `resume` leave long calculations out. |
| `render.py` | Title, chapter and closing cards, the caption band, narration; writes the MP4, SRT, chapter list and poster |

Data: synthetic P and SH shot records written by the CLI `sample` command and the built-in sample
project. No customer data is filmed.

Rebuild after interface changes, from the repository root:

```bash
testenv/start.sh --rebuild-app                                   # builds the image the capture uses
marketing/toolbox/run.sh python marketing/training/narrate.py    # after storyboard text changes
python3 marketing/training/capture.py
marketing/toolbox/run.sh python marketing/training/render.py
```

The voice model is downloaded once into `.tools/media/models` (git-ignored):
`kokoro-v1.0.onnx` (SHA-256 `beb0d1848dee9a49da392cc3df26958d46cfa35d321edf434f52949153f0df3a`) and
`voices-v1.0.bin` (`bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d`) from the
`thewh1teagle/kokoro-onnx` release `model-files-v1.1`. The pointer positions in `storyboard.json` are
for the 1920 × 960 screen; check representative frames before publishing.
