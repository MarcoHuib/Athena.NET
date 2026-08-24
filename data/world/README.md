# World compiler reports

This directory contains compiler audit/capability/unsupported reports only. It
is not a MapServer runtime content directory.

The runtime world is deliberately small and compiled from normal C# under
`src/MapServer/Generated/World`. Pinned `legacy/rathena` source remains the
authoritative upstream and generated definitions are reproducible through
`WorldDataImporter compile` and `compile-script`.

The former bulk `entities/`, developer fixtures, and `warps.json` runtime data
were removed. Expand the compiled world only as each gameplay vertical slice is
implemented and tested.
