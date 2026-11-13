---
pre_release: alpha
depends_on:
  - ../Ranvier.Wgpu/
updaters:
  - regex:
      file: Ranvier.Wgpu.Sdl.fsproj
      pattern: (?<=<Version>)[^<]+(?=</Version>)
  - regex:
      file: README.md
      pattern: (?<=--version ).*
---

# Changelog

This changelog is generated from conventional commits by [EasyBuild.ShipIt](https://github.com/easybuild-org/EasyBuild.ShipIt).
