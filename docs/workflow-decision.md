# lid-guard: workflow decision (xAI Grok API vs n8n)

Date: 2026-10-25. Scope: this project folder only (`Program.cs`, `Ui.cs`, `LockScreen.cs`, `VideoWallpaper.cs`, `JourneyWallpaper.cs`, `IconGen.cs`, `build.ps1`, `uninstall.ps1`). Method: read the source, grep for network, process and registry calls, then check the external facts below. No provider settings were changed, nothing was installed, no data was sent to any provider, and nothing was committed.

## Summary

lid-guard has no AI or LLM workflow. Its only network calls are two GET requests to Open-Meteo (weather and city lookup), with no API key. Neither xAI Grok nor n8n fits. Keep the current approach.

## Workflow table

| # | Workflow | Trigger / schedule | External calls | Data touched | Privacy class | Verdict |
|---|---|---|---|---|---|---|
| 1 | Weather refresh | App start and lock (`Program.cs:454`, `Weather.Refresh`); after a city change (`Ui.cs:776`); a 30-minute staleness check exists (`LockScreen.cs:419`) | `GET api.open-meteo.com/v1/forecast` with lat/lon | Stored city-centre coordinates (from geocoding, not device GPS); returned weather | P2 (personal location, city level) | Neither. Keep Open-Meteo direct. |
| 2 | City lookup | User types a city in Settings (`Ui.cs` city dialog) | `GET geocoding-api.open-meteo.com/v1/search?name=…` | City name text the user typed; returns coordinates | P2 | Neither. Keep. |
| 3 | Lock, unlock, password and key check | Lock hotkey / sign-in; each unlock attempt | None | Password and key hashes (PBKDF2, 100,000 iterations) and salts in `config.txt` | P3 (credentials) | Neither. Must stay local and offline. |
| 4 | Power plan change and restore | Lock and unlock; state saved to `restore.txt` (`Program.cs` ~278–365) | Local `powercfg.exe` calls | Power settings values | P0 | Neither. Local. |
| 5 | Wallpaper render and video loop bake | Lock; settings change; background build when the video or its sections change (`Program.cs:200–218`) | None (local Media Foundation and GDI+) | User-chosen local video path; generated `wallpaper-loop.mp4` | P1 | Neither. Local. |
| 6 | Autostart | Settings toggle | Registry write (HKCU Run key) | Launch command only | P0 | Neither. Local. |
| 7 | Quote, date and clock | Every lock-screen render | None | Static quote list indexed by day of year (`LockScreen.cs:521–539`) | P0 | Neither. Local. |
| 8 | Keyboard hook while locked | Low-level keyboard hook, `SetWindowsHookEx(13)` (`Program.cs:398`) | None found | Keystrokes seen in-process, including password typing | P3 (in-flight secrets) | Neither. Must not log or send keystrokes. Grep found no logging, file or network writes in the hook, but the hook body was not fully reviewed. |

Privacy classes used here:
- **P0**: no personal data.
- **P1**: personal preference or local file path; not sensitive.
- **P2**: personal location or activity (city-level).
- **P3**: credentials, secret hashes, or keystrokes.

## Decision

**xAI Grok API: not for this project.** No workflow needs a language model. Weather and geocoding are structured lookups, and a model would add cost, latency and a third party for no gain. Grok's default 30-day retention would also apply to any prompt sent to it (see the unverified list).

**n8n: not for this project.** The app is a single desktop program that must work offline and while locked. A self-hosted n8n instance (Docker, PostgreSQL, HTTPS reverse proxy) would add a server and a failure point that the lock screen does not need. It would also add a large, actively patched attack surface, and the 2026 CVE reports (third-party only) cite expression-injection RCEs.

**Keep the current approach.** Open-Meteo fetched directly from `WebClient` (TLS 1.2 set in `LockScreen.cs:~413`) covers workflows 1 and 2. The only personal data sent is a city-level coordinate pair.

**If an AI feature is added later** (none exists): route it through the second-brain privacy gateway, as that project already plans, not directly to Grok. Revisit Grok only after the team's retention setting is confirmed in the xAI console.

## Privacy notes

- Coordinates are city-centre points from geocoding, not device location. Grep found no device geolocation API (`Geolocator`, `System.Device`, `Windows.Devices`).
- Open-Meteo states that its webserver logs may hold geographical coordinates and are kept 90 days (from a fetched copy of its terms). A rounded coordinate would reduce this.
- Local secrets are hashed (PBKDF2, 100,000 iterations). Nothing is sent for them.

## Unverified facts

1. **xAI ZDR for this team**: whether zero data retention is available or enabled for this account. Only the public FAQ was read; the console was not checked.
2. **xAI rate limits**: the official per-tier numbers and whether the metric is RPM or RPS. Search summaries disagree. The docs page was not read in full.
3. **xAI prices and flagship model**: from third-party sites only (grok-4.7 at $2 input / $6 output per 1M tokens, and grok-4.6 vs 4.7 as flagship). Not checked against the console.
4. **xAI FAQ wording**: the fetched copy of the security FAQ names the vendor as "SpaceXSI" in several places, probably a text artifact of the fetch. Verify the wording on the live page before quoting it.
5. **n8n `.ee` files**: the fetched n8n FAQ does not mention `.ee` files. The rule that they need a paid Enterprise key comes from search summaries only. License text was not read directly.
6. **n8n CVEs, fixed versions and latest release (2.37.10)**: third-party blogs only. Not checked against n8n's GitHub advisories.
7. **n8n hardware requirements**: not found.
8. **Open-Meteo commercial use**: the free tier is non-commercial. Whether lid-guard is distributed free or paid is not known from the repo. If it is sold, Open-Meteo's terms require contacting them.
9. **Keyboard hook**: not logging keystrokes was checked by grep only, not by reading the whole hook.

## Next step

In `Weather.Fetch` (`LockScreen.cs`, near line 455), round the latitude and longitude to one decimal place (about 11 km) before building the Open-Meteo URL. Then confirm the widget still shows the correct city and temperature. This cuts the stored precision that Open-Meteo logs.
