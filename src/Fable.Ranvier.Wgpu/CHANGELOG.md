---
force_version: 0.1.0
include:
  - ../Ranvier.Wgpu/Types.fs
  - ../Ranvier.Wgpu/Primitives.fs
  - ../Ranvier.Wgpu/Shaders.fs
  - ../Ranvier.Wgpu/ScenePass.fs
updaters:
  - regex:
      file: Fable.Ranvier.Wgpu.fsproj
      pattern: (?<=<Version>)[^<]+(?=</Version>)
  - regex:
      file: README.md
      pattern: (?<=--version ).*
---

# Changelog

This changelog is generated from conventional commits by [EasyBuild.ShipIt](https://github.com/easybuild-org/EasyBuild.ShipIt).
