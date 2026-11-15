## What and why

<!-- What does this change, and why? Link the issue: "Fixes #123". English or French. -->

## How it was tested

<!-- Real hardware matters here: what did you run, and on what? -->

- PC (graphics card, Windows version):
- TV (model):
- What I checked:

## Checklist

- [ ] Both apps build (`dotnet build windows/TVScreenPlus -c Release`, `cd android && ./gradlew assembleRelease`).
- [ ] An older TV app and an older PC program still work with this change (protocol compatibility).
- [ ] No extra work per frame on the video path, no new dependency; the TV APK stays under 150 KB.
- [ ] New TV texts are in all four languages of `L.kt` (or I asked for help with them).
- [ ] `README.md` and `README.fr.md` are updated if the behaviour changed.
- [ ] `VERSION`, the CI workflow and the signing setup are untouched.
- [ ] I have read, built and tested the code myself (including any AI-assisted parts).
