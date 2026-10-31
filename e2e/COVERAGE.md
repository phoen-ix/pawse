# E2E coverage map

Every user-facing feature of the app and the website, mapped to the test that exercises it.
Run everything with `scripts/e2e.sh` (the app half on Windows only; CI job `e2e` in ci.yml).

- **App** (`A`): `tests/Pawse.Tests/E2E/*.cs`, xunit, trait `Category=E2E`. Drives the real
  windows on an STA thread through UI Automation peers, routed input events and the tray's
  own callback message; message boxes are read and answered by `DialogWatch` (`Ui.cs`).
  Listed (Windows) with `dotnet test tests/Pawse.Tests/Pawse.Tests.csproj --list-tests --filter-trait "Category=E2E"`.
- **Site** (`S`): `e2e/tests/site.spec.ts`, Playwright (`cd e2e && npx playwright test --list`).
- One role each: the app has a single local user (installed vs portable copy and locked vs
  unlocked are covered as states); the site is public and static (guest).

Extraction (2026-10-31: **66 items**; the state/flow rows below the item table are added by
hand from the code - warnings, guards, lock state, deployment mode, App-level flows):

```bash
{ grep -ohE 'x:Name="(Chk|Rb|Btn|Txt|Sld|Cmb)[A-Za-z]+"|Click="On[A-Za-z]+"|RequestNavigate="On[A-Za-z]+"' src/Pawse/UI/*.xaml | sort -u
  grep -oE 'ToolStripMenuItem\("[^"]+"' src/Pawse/UI/TrayIcon.cs
  grep -ohE 'class [A-Za-z]+ : (Window|TextBox|IDisposable)' src/Pawse/UI/*.cs
  grep -oE 'aria-labelledby="[a-z]+"' web/site/index.html
  ls web/site | grep -vE '\.(png|svg)$'; }
```

Totals: **66 extracted items, all ✅. 54 state/flow rows: ✅ 39, ❌ 15** - (a) 1, (b) 1, (c) 7, (d) 3,
(e) 3. Tests: app 54 methods / 82 cases, site 15.

## Extracted items

| Item | What | Test | |
|---|---|---|---|
| `class SettingsWindow` | the Settings window, 5 pages | A `SettingsE2ETests.Every_page_opens_on_its_title` (×5) | ✅ |
| `class OverlayWindow` | the lock popup | A `OverlayE2ETests.*` | ✅ |
| `class ShortcutRecorderWindow` | Set… recorder | A `RecorderE2ETests.*` | ✅ |
| `class ChordBox` | the two shortcut boxes | A `SettingsE2ETests.A_shortcut_is_recorded_by_pressing_it_in_the_box` | ✅ |
| `class TrayIcon` | the tray paw | A `TrayE2ETests.*` | ✅ |
| `ChkStartLocked`, `ChkTrayDoubleClick`, `ChkLogging` | General | A `SettingsE2ETests.A_checkbox_is_saved_into_the_config` | ✅ |
| `ChkAutostart` | start at sign-in | A `SettingsE2ETests.Start_at_sign_in_is_reported_only_when_changed` | ✅ |
| `ChkLockHotkey`, `ChkBlockMouse`, `ChkBlockScreenKeyboard`, `ChkWinLock`, `ChkLaunchMedia` | Locking | A `SettingsE2ETests.A_checkbox_is_saved_into_the_config` | ✅ |
| `ChkChord`, `ChkLockphrase`, `ChkResetWrong`, `ChkMouseHold`, `ChkTimer` | Unlocking | A `SettingsE2ETests.A_checkbox_is_saved_into_the_config` | ✅ |
| `ChkOverlay`, `ChkPawHint`, `ChkShowLockphrase` | Lock popup | A `SettingsE2ETests.A_checkbox_is_saved_into_the_config` | ✅ |
| `TxtLockHotkey`, `TxtChord` | record by pressing keys | A `SettingsE2ETests.A_shortcut_is_recorded_by_pressing_it_in_the_box` | ✅ |
| `BtnSetLockHotkey`, `BtnSetChord` / `OnSetLockHotkey`, `OnSetChord` | Set… | A `SettingsE2ETests.Set_opens_the_recorder_and_takes_what_it_captured` | ✅ |
| `OnClearLockHotkey`, `OnClearChord` | Clear | A `SettingsE2ETests.Clear_empties_a_shortcut` | ✅ |
| `TxtLockphrase`, `TxtHoldMs`, `TxtTimerSeconds` | text fields | A `SettingsE2ETests.Lockphrase_and_durations_are_saved` | ✅ |
| `CmbDisplayMode` | All / Selected displays | A `SettingsE2ETests.Display_list_is_live_only_for_selected_displays` | ✅ |
| `RbStyleWindow`, `RbStylePaw`, `SldOpacity`, `SldHorizontal`, `SldVertical`, `SldPawSize` | popup look and place | A `SettingsE2ETests.Popup_style_position_opacity_and_size_are_saved` | ✅ |
| `RbUpdManual`, `RbUpdNotify`, `RbUpdAuto` | update mode | A `SettingsE2ETests.The_update_mode_is_saved` (×3) | ✅ |
| `BtnCheckUpdates`, `BtnDownloadsPage` | Check now / Try again / Downloads page | A `SettingsE2ETests.Check_now_reports_progress_failure_and_result` | ✅ |
| `BtnBannerUnlock` / `OnBannerUnlock` | locked banner's Unlock | A `SettingsE2ETests.While_locked_the_banner_shows_and_typing_fields_are_off` | ✅ |
| `OnSave` | Save | A `SettingsE2ETests.A_checkbox_is_saved_into_the_config` (and every save test) | ✅ |
| `OnCancel` (Settings) | Cancel | A `SettingsE2ETests.Cancel_leaves_the_config_untouched` | ✅ |
| `OnCancel` / `BtnCancel` (recorder) | Cancel / Close | A `RecorderE2ETests.Cancel_records_nothing`, `A_refused_key_is_explained_and_Close_closes` | ✅ |
| `OnOpenLink` | About links (targets) | A `SettingsE2ETests.About_names_the_version_and_links_home` | ✅ |
| `ToolStripMenuItem("Lock now"` | Lock now / Unlock | A `TrayE2ETests.Lock_now_locks_and_the_menu_and_tooltip_follow` | ✅ |
| `ToolStripMenuItem("Settings…"`, `"Open config file"`, `"Quit"`, `"Restart as administrator"` | menu → App | A `TrayE2ETests.Each_menu_item_asks_the_app_once_the_menu_has_closed` (admin item only when not elevated: `The_menu_offers_lock_settings_config_and_quit`) | ✅ |
| `aria-labelledby="what"`, `"unlocking"`, `"download"`, `"limits"`, `"questions"` | home sections | S `home › every section is there under its own anchor` | ✅ |
| `index.html`, `privacy.html` | the two pages, desktop + phone | S `sweep › …` (×4) | ✅ |
| `latest.json` | update feed shape | S `update feed › latest.json is the shape the app checks downloads against` | ✅ |
| `robots.txt` | feed kept out, sitemap named | S `search engines › robots.txt keeps the feed out and names the sitemap` | ✅ |
| `sitemap.xml` | lists existing pages | S `search engines › every page in the sitemap exists` | ✅ |

## States, guards and flows

| Feature / state | Test | |
|---|---|---|
| Duration boxes accept digits only (typed and pasted) | A `SettingsE2ETests.Duration_boxes_take_digits_only` | ✅ |
| Out-of-range durations warn, clamp on save | A `Out_of_range_durations_warn_and_are_clamped_on_save`, `A_valid_duration_clears_its_warning` | ✅ |
| Lockphrase warnings (empty, untypeable, only when on), trimmed on save | A `Lockphrase_warnings_follow_what_is_typed`, `Lockphrase_and_durations_are_saved` | ✅ |
| Shortcut box: Esc keeps the old one | A `Esc_in_a_shortcut_box_keeps_the_old_shortcut` | ✅ |
| Shortcut box: unusable key refused, old one kept | A `A_key_that_cannot_be_a_shortcut_is_refused_and_the_old_one_kept` | ✅ |
| Shortcut box: modifiers-only warning | A `A_modifiers_only_shortcut_warns` | ✅ |
| Recorder cancelled → shortcut unchanged | A `Set_opens_the_recorder_and_takes_what_it_captured` | ✅ |
| Paw options shown only for the paw style | A `Popup_style_position_opacity_and_size_are_saved` | ✅ |
| Unplugged displays survive a save | A `Displays_that_are_unplugged_survive_a_save` | ✅ |
| No display ticked → popup off + message | A `Ticking_no_display_switches_the_popup_off_and_says_so` | ✅ |
| Every display ticked → All displays + message | A `Ticking_every_display_becomes_all_displays_and_says_so` | ✅ |
| Unrelated save says nothing about displays (bug fixed) | A `An_unrelated_save_says_nothing_about_displays` | ✅ |
| Lockout guard: chord re-enabled + message; reseeded to Ctrl+L | A `Saving_without_a_working_unlock_method_turns_the_chord_back_on`, `An_empty_chord_is_reseeded_to_Ctrl_L_by_the_lockout_guard` | ✅ |
| Locked: banner, typing fields off, Unlock asks App | A `While_locked_the_banner_shows_and_typing_fields_are_off` | ✅ |
| Locked: no shortcut recording (box and Set…) | A `While_locked_no_shortcut_can_be_recorded` | ✅ |
| Unlocked while open: fields back, stale hint cleared | A `An_unlock_while_open_gives_the_fields_back` | ✅ |
| Portable copy: registry options greyed with reason, values kept | A `A_portable_copy_greys_out_what_would_touch_the_registry_and_keeps_its_values` | ✅ |
| Installed copy: every option offered | A `An_installed_copy_offers_every_option` | ✅ |
| About: version line, dev build | A `About_names_the_version_and_links_home` | ✅ |
| Update panel: Checking…, progress, failure (Try again + Downloads page), result; last check time | A `Check_now_reports_progress_failure_and_result`, `The_last_check_time_is_shown_in_local_time` | ✅ |
| Popup hint per unlock mix (7 variants) and hold area shown only when usable | A `OverlayE2ETests.The_card_names_only_the_ways_out_that_work` (×7) | ✅ |
| Popup auto-unlock line | A `The_card_counts_down_when_the_timer_is_on` | ✅ |
| Paw look: size, hint box, timer line | A `The_paw_look_has_its_size_hint_and_timer` | ✅ |
| Opacity floor | A `Opacity_never_drops_below_the_floor` | ✅ |
| Hold the button / the paw unlocks once | A `Holding_the_button_or_the_paw_unlocks` (×2) | ✅ |
| Early release / leaving the button cancels the hold | A `Letting_go_early_or_leaving_the_button_does_not_unlock` | ✅ |
| Mouse blocked: hold does nothing | A `With_the_mouse_blocked_a_hold_does_nothing` | ✅ |
| Alt+F4 hides the popup; App's close closes it | A `Alt_F4_only_hides_the_popup_until_the_app_lets_it_close` | ✅ |
| Recorder: hold keys, let go in any order, shows the chord | A `RecorderE2ETests.Holding_keys_then_letting_go_records_them` | ✅ |
| Recorder: Esc cancels (bug fixed) | A `Esc_cancels_instead_of_becoming_the_shortcut`, `Esc_also_cancels_a_chord_half_pressed` | ✅ |
| Recorder: refused key explained, Close closes (bug fixed) | A `A_refused_key_is_explained_and_Close_closes` | ✅ |
| Tray: one click locks | A `TrayE2ETests.One_click_locks` | ✅ |
| Tray locked: one click only hints (balloon) | A `While_locked_one_click_only_says_how_to_unlock` | ✅ |
| Tray locked: double-click unlocks, hint called off | A `While_locked_a_double_click_unlocks` | ✅ |
| Tray: fast double-click while unlocked only locks | A `A_fast_double_click_while_unlocked_only_locks` | ✅ |
| Tray: classic single-click mode | A `With_double_click_off_one_click_unlocks_too` | ✅ |
| Tray: right-click opens the menu | A `A_right_click_opens_the_menu` | ✅ |
| Site: no scripts, no cookies, nothing from other sites (privacy page's promise) | S `privacy › the site keeps its own promise…` + every test's guard | ✅ |
| Site: hero download/source, download rows, unlock defaults, footer nav, previews/icon/JSON-LD, privacy sections | S `home › …`, `privacy › covers the app…` | ✅ |
| Locking/unlocking through the keyboard hook (hotkey, chord, lockphrase, on-screen keyboards, stuck modifiers) | ❌ (c) `LockControllerTests` (incl. `The_unlock_chord_can_be_tapped_on_an_on_screen_keyboard`), `ChordMatcherTests`, `ChordMatcherExactTests`, `LockphraseMatcherTests` (`MatchersTests.cs`). A future e2e route: SendInput into a running App - the hook passes injected keys to the matchers on purpose | ❌ |
| Mouse block, Win+L block, media-key block applied while locked | ❌ (c) ci.yml `store` (Win+L value outside the package, swept after a kill), `installer-smoke` portable step; `WorkstationLockTraceTests` | ❌ |
| Keyboard Filter engaged while locked (Locking page, consumer keys that bypass the hook) | ❌ (a) needs Windows Enterprise/Education/IoT with the Keyboard Filter feature and an elevated Pawse; the runner has neither | ❌ |
| Startup: StartLocked, single instance / take-over over the quit channel, installer quit | ❌ (c) ci.yml `installer-smoke` (upgrade while running, uninstall, portable StartLocked), `QuitSignalTests` | ❌ |
| Startup: broken config gets a working unlock + balloon | ❌ (c) `EnsureUsableUnlockFallbackTests`, `HasUsableUnlockTests` | ❌ |
| Autostart repair, portable writes nothing, uninstall leaves nothing | ❌ (c) ci.yml `installer-smoke`, `UninstallCleanupTests`, `DataFolderTests` | ❌ |
| Self-update: plan, download, verify, swap | ❌ (c) `UpdatePlanTests`, `UpdateHashTests`, `UpdateSumsTests`, `SelfReplaceSwapTests`, `StartupSignalTests` | ❌ |
| Config persistence (pawse.json round trip, hand edits) | ❌ (c) `ConfigJsonTests`, `ConfigDefaultsTests` | ❌ |
| Check now against GitHub / pawse.at, installing the download | ❌ (b) reaches github.com and runs an installer; no sink - the panel's states are ✅ above | ❌ |
| Restart as administrator | ❌ (d) the UAC prompt is on the secure desktop | ❌ |
| Open config file, About links in the browser | ❌ (d) handed to the shell's default editor / browser (targets ✅ above) | ❌ |
| Balloon pop-ups as shown by the shell | ❌ (d) drawn by Explorer; the tray's balloon text is ✅ above | ❌ |
| App wiring: tray toggle → LockController; lock → popups on the chosen displays, hidden on unlock; auto-unlock timer | ❌ (e) needs an App-level harness (App is a process singleton that installs hooks and a mutex); ~3 h. Pieces ✅ above and in `LockControllerTests` | ❌ |
| App wiring: Settings opened once from the tray and re-surfaced; Save applied (matchers, mouse hook, autostart write, overlays rebuilt, save-failure balloon) | ❌ (e) same harness; ~2 h | ❌ |
| App: Quit while locked asks first | ❌ (e) same harness; the dialog itself is drivable with `DialogWatch`; ~30 min once it exists | ❌ |
