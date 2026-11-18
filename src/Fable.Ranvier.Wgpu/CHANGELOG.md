---
last_commit_released: d5af73cabb4023f0d369b9ace7c69680b1f77e59
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

## 0.2.0 - 2026-11-18

### 🚀 Features

* *(wgpu)* Add orthographic projection, dynamic mesh updates, and sub-range batch writes (#13) ([d5af73c](https://github.com/ranvier-org/Ranvier.Wgpu/commit/d5af73cabb4023f0d369b9ace7c69680b1f77e59))

<strong><small>[View changes on Github](https://github.com/ranvier-org/Ranvier.Wgpu/compare/2fc79723e7a8e4aa9a9cfea3c1547e0f006de99e..d5af73cabb4023f0d369b9ace7c69680b1f77e59)</small></strong>

## 0.1.1 - 2026-11-16

### 🐞 Bug Fixes

* *(fable)* Lower Fable.Ranvier.Wgpu's minimum FSharp.Core to 10.0.100 (#10) ([2fc7972](https://github.com/ranvier-org/Ranvier.Wgpu/commit/2fc79723e7a8e4aa9a9cfea3c1547e0f006de99e))

<strong><small>[View changes on Github](https://github.com/ranvier-org/Ranvier.Wgpu/compare/2f9b86b7544e87f8cb7adfd3410bfd4105655587..2fc79723e7a8e4aa9a9cfea3c1547e0f006de99e)</small></strong>

## 0.1.0 - 2026-11-15

### 🚀 Features

* Initial Ranvier.Wgpu and Ranvier.Wgpu.Sdl engine and zero-copy WebCore bridge ([de74dbb](https://github.com/ranvier-org/Ranvier.Wgpu/commit/de74dbbc0d4bfd5faf079ecc632e193047aea9bb))
* *(wgpu)* Draw lines and discs under custom WGSL, add user uniforms, shader hooks and WGSL validation (#4) ([a764896](https://github.com/ranvier-org/Ranvier.Wgpu/commit/a7648963dbd93bb009e9853cd296c7b9d2ccd2c4))
* *(wgpu)* Add Fable.Ranvier.Wgpu, the scene API on browser WebGPU (#6) ([9f82215](https://github.com/ranvier-org/Ranvier.Wgpu/commit/9f82215067e7f1d4d7d7d214fabf7158d823f7a9))
* *(wgpu)* Add a WebGL2 backend to Fable.Ranvier.Wgpu and pack it (#7) ([2f9b86b](https://github.com/ranvier-org/Ranvier.Wgpu/commit/2f9b86b7544e87f8cb7adfd3410bfd4105655587))
