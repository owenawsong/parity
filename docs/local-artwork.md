# Local artwork and motion tools

There is an opt-in importer for Apple's Pro 3 artwork from the [SharingDeviceAssets catalog](https://mesu.apple.com/assets/com_apple_MobileAsset_SharingDeviceAssets/com_apple_MobileAsset_SharingDeviceAssets.xml). Pick the white `AirPodsPro1,3-v2` ZIP. The Home view uses the separate earbud and case images. The motion gallery prepares the larger Pro 3 lift-out. Run `python tools/import-airpods-connect.py` with FFmpeg 9 or newer for the other models' short connection clips. Build the UI after generating the clips so it can play the matching motion after a confirmed connection.

The nearby card can use Apple's six-second rotations for AirPods 1–5, AirPods 4 (ANC), AirPods Pro 1–3, and AirPods Max 1–2. Where Apple provides a charging rotation, the popup selects it when the case reports charging. The importer prepares transparent WebM clips and matching still images for reduced motion. The popup preview includes a model selector and a case-charging toggle.

The Overview importer trims the opening to the upright pose and matches it to the turn on original frames. It stages the confirmation using the model's feature clip where supplied. AirPods 1 and 2 use a layered lift made with the model's case and individual earbud images.

With Pillow, NumPy, OpenCV, and FFmpeg 9 or newer, prepare the media from the repository root. Then build the UI and preview with the local assets enabled.

```powershell
python tools/import-pro3-art.py path/to/pro3-apple.zip
python tools/import-airpods-popup-models.py --catalog path/to/catalog.xml
python tools/import-airpods-overview.py
cd ui
npm run build -- --local-pro3-art
node preview/build.mjs --local-pro3-art
```

The model importer can also use the catalog saved in `.build/pro3-reference/catalog.xml` if you omit `--catalog`. It downloads each model's assets and writes the processed media to the ignored `.build` folder. The local build and preview contain Apple artwork. Default builds use the fork's artwork. Confirm redistribution rights before publishing a build containing the Apple artwork.

## Local motion gallery

After building the opt-in app previews, run the commands below from the repository root with the same Apple ZIP and FFmpeg 9 or newer. The gallery serves only on loopback and includes the six source clips, the editable lid prototype, and both simulated app previews.

```powershell
python tools/build-motion-gallery.py path/to/pro3-apple.zip
node tools/serve-motion-gallery.mjs 8787
```

Open `http://localhost:8787/flow.html` to replay discovery and connection, compare popup battery layouts, and test the static in-app charging indicators. The source clips are at `http://localhost:8787/`. The gallery output is under `dist/motion-gallery/`.
