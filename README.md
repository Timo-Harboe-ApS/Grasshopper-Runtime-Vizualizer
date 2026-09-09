# Grasshopper Runtime Visualizer

Local Rhino/Grasshopper debugging plugin for recording runtime expiration activity and replaying it in a browser heatmap.

## Version 0.1 scope

- Grasshopper component: `Runtime > Debug > Runtime Recorder`
- Disk-backed sessions in `~/Documents/GrasshopperRuntimeVisualizer/Sessions`
- Captures document graph, component canvas bounds, loose params, component input/output param endpoints, param-to-param wires, groups, solution boundaries, expiration events, document add/remove events, and selected object changes
- Local HTTP/WebSocket browser visualizer on `http://127.0.0.1:8787/` or the next free port
- Live heat flashes, optional per-object expiration badges, timeline playback, stepping, speed control, fit view, and jump to last activity
- Visible server/session version in the browser header
- Debug mode with component diagnostics, `/debug`, `/api/debug`, Rhino command-line logging, and `~/Documents/GrasshopperRuntimeVisualizer/RuntimeVisualizer.log`

## Build

```bash
dotnet build
```

The Grasshopper plugin output is:

```text
src/GrasshopperRuntimeVisualizer/bin/Debug/net8.0/GrasshopperRuntimeVisualizer.gha
```

Release builds generate a timestamped Yak package version, for example `2026.909.1094406123`, and write that version into the Yak `manifest.yml` beside the built `.gha`. The format is `yyyy.Mdd.1HHmmssfff`, which keeps each built package version higher than the previous one as long as the machine clock moves forward.

To build and package with the timestamped Yak version:

```bash
./scripts/package-release.sh
```

## Load in Grasshopper

Use `_GrasshopperDeveloperSettings` in Rhino and add the build output folder above. If you install manually, copy the `.gha` and the adjacent `Resources` folder together so the browser visualizer can be served.

This project is pinned to `Grasshopper` / `RhinoCommon` `8.33.26188.13001`, matching the installed Rhino 8.33 line on this machine.

## Basic use

1. Drop `Runtime Recorder` onto the Grasshopper canvas.
2. Set `Record` to `true`.
3. Optionally pulse `Open Visualizer` to open the browser.
4. Run the definition or trigger the problematic operation.
5. Set `Record` to `false` to stop cleanly.

If Rhino or Grasshopper freezes, reopen the visualizer after recovery. The latest session remains on disk and the timeline will show if the recording ended during an incomplete solution.

## Debugging local server issues

If the browser shows a 404, first use the `Visualizer URL` output from the component rather than assuming port `8787`. If another Rhino process or an older plugin instance is still using that port, the new server will bind to the next free port.

Turn on the `Debug Mode` input and inspect:

- `Debug URL` output, usually `http://127.0.0.1:8787/debug`
- `Diagnostics` output on the component
- `~/Documents/GrasshopperRuntimeVisualizer/RuntimeVisualizer.log`

The root page is also embedded into the `.gha` as a fallback, so `/` should still load even if the copied `Resources/browser/index.html` file cannot be found from Rhino's plugin load context.

## Current visualization timing

- Expiration badges are hidden by default. Use `Counts` to show cumulative visible canvas expirations for each object at the current timeline position.
- Heat cooldown is approximately 1 second after the last expiration.
- Internally the heat curve uses exponential decay with a 0.25 second time constant and drops tiny values below `0.02`. One visible expiration is enough to reach full heat, then the object cools from there.
- Live mode advances with the browser animation clock between incoming Grasshopper events, so heat cools continuously even when no new event arrives.
- `Blink delay` defaults to `0 ms`. Values above zero set a minimum visible gap between near-identical expiration flashes without changing the recorded timestamps. Equal timestamps still flash together.
- Heat and visible counts follow the delayed visual expiration schedule, so cooldown starts when the delayed blink appears.
- The recorder subscribes to native top-level Grasshopper document objects. Grasshopper's `SolutionExpired` event is raised on the top-level object, so owned component input/output params are treated as graph endpoints rather than inferred expiration sources.
- Raw `SolutionExpired` callbacks are still recorded as `expire` events. When available, the canvas heat, blinks, and optional badges use `canvas_expire` events, which are emitted only when the object's native GH solution phase transitions into `Blank`.
- Native `ObjectChanged` callbacks are recorded as `object_changed` events and draw a separate teal pulse, so source params/sliders can be visible even when they are not themselves expired.
- Raw callbacks and object changes remain in the event stream for debugging; the canvas stays focused on heat, flashes and playback.
