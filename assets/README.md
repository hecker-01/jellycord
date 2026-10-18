# Assets

| File | Used for |
|---|---|
| `logo.svg` / `logo.png` | Plugin logo; default Discord image when there's no poster |
| `banner.png` (source: `src/banner.html`) | Jellyfin plugin catalog image and README header |
| `playing.png`, `paused.png` | Small status badge on the Discord artwork |
| `preview-*.png` | Real Discord screenshots for the README |

The plugin loads `logo.png`, `playing.png` and `paused.png` from `raw.githubusercontent.com/hecker-01/jellycord/main/assets/`, so keep those paths stable.

Re-render PNGs with headless Chrome, e.g.:

```bash
chrome --headless=new --hide-scrollbars --allow-file-access-from-files --default-background-color=00000000 --window-size=512,512 --screenshot=logo.png logo.svg
```

```bash
chrome --headless=new --hide-scrollbars --allow-file-access-from-files --window-size=1280,720 --screenshot=banner.png src/banner.html
```
