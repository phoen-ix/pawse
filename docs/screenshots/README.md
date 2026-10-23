# Screenshots

Every window Pawse has, and the website. Made by the docs tour - see
[Regenerating](#regenerating) - from a fixture config: the defaults plus the lockphrase
`whiskers`. The app shots show a window's contents without the Windows title bar.

## Lock popup - window style

Shown while locked, on the displays you pick. It names every unlock method that works in the
current settings; hold the button to unlock with the mouse.

<img src="app-lock-popup.png" alt="The lock popup: Keyboard locked, how to unlock, and a Hold to unlock button" width="460">

## Lock popup - paw icon style

The same popup as the tray paw, at any size, with an optional hint box beneath. Hold the paw
to unlock: it fills green from the bottom.

<img src="app-lock-popup-paw.png" alt="The paw-icon lock popup with its unlock hint beneath" width="335">

## Tray menu

Right-click the paw in the system tray. "Restart as administrator" is added when Pawse isn't
already elevated. (This one is drawn by WinForms at the screen's own scale, so it is 1x.)

<img src="app-tray-menu.png" alt="Tray menu: Lock now, Settings…, Open config file, Quit">

## Settings - General

Lock on start, start with Windows, the tray double-click guard and the opt-in log file.

<img src="app-settings-general.png" alt="Settings, General page" width="784">

## Settings - Locking

The lock hotkey, what else to block while locked (mouse, on-screen keyboards), and the system
keys: Win+L and the browser / calculator / media keys.

<img src="app-settings-locking.png" alt="Settings, Locking page" width="784">

## Settings - Unlocking

The four unlock methods, each switched on or off on its own: a key combination, a lockphrase,
holding the popup button or paw, and an automatic timer.

<img src="app-settings-unlocking.png" alt="Settings, Unlocking page with chord, lockphrase, mouse hold and timer" width="784">

## Settings - Lock popup

Whether the popup shows and where: all displays or selected ones, its style, opacity and
position.

<img src="app-settings-lock-popup.png" alt="Settings, Lock popup page with the window style picked" width="784">

With **Paw icon** picked, the paw's size and its hint box appear.

<img src="app-settings-lock-popup-paw.png" alt="Settings, Lock popup page with the paw icon style and its options" width="784">

## Settings - About

The version, links, and how far Pawse may go about updates on its own. **Only when I ask** is
the default; **Check now** is the only thing that goes online.

<img src="app-settings-about.png" alt="Settings, About page with the three update levels and Check now" width="784">

## Settings while locked

Opened while Pawse has the keyboard locked: a banner says why the text fields are disabled,
and its **Unlock** button is the way out with the mouse.

<img src="app-settings-locked.png" alt="Settings with the yellow keyboard-locked banner and disabled fields" width="784">

## Settings in a portable copy

A portable copy writes nothing to the registry, so starting with Windows is greyed out, with
the reason beside it. The Locking page does the same for Block Win+L and the media-key block.

<img src="app-settings-portable.png" alt="Settings, General page of a portable copy with start-with-Windows greyed out" width="784">

## Recording a shortcut

**Set…** next to a shortcut opens the recorder: hold the keys, let go, and it takes what you
held. Esc cancels.

<img src="app-shortcut-recorder.png" alt="Shortcut recorder showing Ctrl+Shift+U" width="440">

## Website - pawse.at

The home page's first screen ([`web/site/index.html`](../../web/site/index.html)), cropped to
the hero column.

<img src="site-home.png" alt="pawse.at home page hero: logo, title, tagline and download button" width="660">

On a phone:

<img src="site-home.mobile.png" alt="pawse.at home page on a phone" width="390">

The privacy page ([`web/site/privacy.html`](../../web/site/privacy.html)).

<img src="site-privacy.png" alt="pawse.at privacy page, top" width="808">

## Regenerating

`scripts/docs-screenshots.sh [outdir]` (default: this folder) runs both halves of the tour:

- **Website** (`site-*.png`): serves `web/site` on `127.0.0.1:$((PORT_BASE+2))` (PORT_BASE
  default 25950) and runs the Playwright tour in [`e2e/docs/`](../../e2e/docs/). Needs Python,
  and Node with `npm ci && npx playwright install chromium` in `e2e/`.
- **App** (`app-*.png`), Windows only:
  [`DocsScreenshotTests`](../../tests/Pawse.Tests/DocsScreenshotTests.cs), an xunit test that
  opens each window on an STA thread and renders it at 2x. It only runs when
  `DOCS_SCREENSHOT_DIR` is set, so a plain `dotnet test` skips it.

The committed set comes from the **docs-screenshots** workflow (Actions → docs-screenshots →
Run workflow, on `windows-latest`): download its `docs-screenshots` artifact, unzip it here,
optimise with `oxipng -o 4 --strip safe *.png`, check the diff, commit. `ci.yml` runs both
halves on every push as a render check and throws the PNGs away.
