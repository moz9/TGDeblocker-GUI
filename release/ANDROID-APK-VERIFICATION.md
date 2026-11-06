# Android 1.0.2 — 2026-11-06

ARM64 APK: `dist/Android/TGDeblocker.apk`, 31,636,421 bytes, versionCode 3.
SHA-256: `8773CD5ADD2AA678645748B7D84D691D285EDD3079AFAC505C03FE42901DCC6A`.
Publisher certificate unchanged: `6f0301a75bf6e25435d56fbb49685ab1eb5d6e2cbe1b2ca544c3f96a2a38bcb6`.

- ARM64 and x86_64 Release builds passed. APK signature v2/v3, launcher resources, ABI and 16 KiB alignment checks passed. The iOS managed compile passed; no IPA or iOS runtime check is claimed.
- The matching x86_64 Release ran in the dedicated API 36 emulator. Checks covered navigation drag, reversal, cancellation and release; demo forms and background/return; RU/EN; light/dark themes; bundled publisher API configuration; no application crash markers.
- A Debug-only delayed demo load verified rapid B–C–B navigation and background/resume of the latest queued page. Mutating Telegram actions still lock navigation.
- Final material uses a cached GPU Gaussian blur before refraction of the app's real content. A/B diagnostics changed 9,814 dock pixels for the lens and 36,308 for blur, with zero changed control pixels outside the glass region. The earlier sparse-sampling candidate was rejected for text ghosting.
- Final Release checks repeated live rendering, both themes, 320 dp Russian layout and navigation motion. Content can scroll fully above the dock. A publisher-signed reinstall retained the selected language and theme.
- Held slider checks used actual horizontal movement: the middle lens expands; at the ends its radius is constrained while its centre stays aligned with the native value. Opaque surfaces disable the slider; reduced motion keeps the dock selection at its resting size.
- Argon reviewed interaction risks; Terra reviewed the demo, queue handling and captured motion/layout. The original C++ and shared Telegram core were not changed.

Evidence: `artifacts/android-1.0.2-*.log`, `android-1.0.2-package.json`, `android-1.0.2-final/`, `android-1.0.2-motion/`, `android-nav-gaussian-optics/` and `android-nav-queue.log`.

Limits: physical ARM64 devices, phone GPU performance and real Telegram authentication/session persistence were not tested in this pass. Live content refraction requires Android 13+; Android 8–12 retain the simplified fallback. Windows 1.1.0 is unchanged. Android updates remain manual APK installs.
