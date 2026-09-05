# RavaCast

Dalamud plugin for shared in-world browser screens and Direct Stream.

## Build

Open `RavaCast.sln` and build the `RavaCast` project in `Release`.

The renderer and Direct Stream projects are included in this repository:

- `RavaCast.Renderer`
- `RavaCast.Media.Native`
- `RavaCast.Media.BridgeHost`
- `RavaCast.Media.Runtime`

The plugin build publishes and validates the renderer/media bundle before Dalamud packaging.
