# tests/

Unit tests live here. They should be runnable headlessly on CI.

```
tests/
├── RokidDiyStation.Core.Tests/
│   ├── Fusion/
│   ├── Parsing/
│   └── Protocols/
└── RokidDiyStation.IntegrationTests/
    └── (require a real Rokid Max)
```

Initial focus: **fusion** and **parsing** — these are pure functions that don't need real hardware.

`IntegrationTests` are opt-in (a `[Trait("Category","Hardware")]` marker) and won't run on CI; only on a contributor's desk.
